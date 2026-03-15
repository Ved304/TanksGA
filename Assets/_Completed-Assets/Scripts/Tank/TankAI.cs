using Complete;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Controls AI behaviour for enemy tanks in the Tanks! tutorial project.
/// This script is added dynamically at runtime when "Is Computer Controlled"
/// is enabled on the TankMovement component.
///
/// Behaviour states:
///   Chase  – navigate toward the closest enemy tank.
///   Attack – stop, charge a shot, and fire when a clear line of sight exists.
///   Flee   – pick a random direction away from the target and move there.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class TankAI : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Private state machine enum
    // -------------------------------------------------------------------------
    private enum State
    {
        Chase,
        Attack,
        Flee
    }

    // -------------------------------------------------------------------------
    // Serialized fields (editable in the Inspector on the prefab variant)
    // -------------------------------------------------------------------------
    [Header("Shooting")]
    [Tooltip("The AI will begin charging a shot when the target is within this distance.")]
    [SerializeField] private float m_MaxShootingDistance = 15f;

    [Tooltip("Minimum time (in seconds) the AI must wait between shots.")]
    [SerializeField] private float m_ShotCooldownTime = 2f;

    [Header("Fleeing")]
    [Tooltip("If the target has not moved more than this distance within the flee-check window, the AI considers itself in danger and flees.")]
    [SerializeField] private float m_StationaryThreshold = 0.5f;

    [Tooltip("How long (in seconds) the AI watches the target before deciding to flee.")]
    [SerializeField] private float m_StationaryCheckTime = 3f;

    // -------------------------------------------------------------------------
    // Private references – assigned in Awake / Start
    // -------------------------------------------------------------------------
    private NavMeshAgent m_NavMeshAgent;
    private TankShooting m_Shooting;         // sibling component on this tank
    private TankHealth m_Health;           // sibling component on this tank

    // All tanks currently in the scene (populated by the GameManager or found at Start)
    private TankHealth[] m_AllTanks;

    // -------------------------------------------------------------------------
    // Runtime state
    // -------------------------------------------------------------------------
    private State m_CurrentState = State.Chase;
    private Transform m_CurrentTarget;

    private NavMeshPath m_CurrentPath;
    private int m_CurrentCorner = 1;   // index into m_CurrentPath.corners[]
    private bool m_IsMoving = false;

    private float m_ShotCooldown = 0f;

    // Used for the "has the target been stationary?" check
    private Vector3 m_LastTargetPosition;
    private float m_StationaryTimer = 0f;

    // -------------------------------------------------------------------------
    // Unity lifecycle
    // -------------------------------------------------------------------------
    private void Awake()
    {
        m_NavMeshAgent = GetComponent<NavMeshAgent>();
        m_Shooting = GetComponent<TankShooting>();
        m_Health = GetComponent<TankHealth>();
        m_CurrentPath = new NavMeshPath();

        // Disable the built-in agent movement – we drive rotation & movement
        // ourselves via TankMovement, so we only use the agent for path calculation.
        m_NavMeshAgent.updatePosition = false;
        m_NavMeshAgent.updateRotation = false;
    }

    private void Start()
    {
        // Gather all tanks in the scene (including this one; we skip ourselves later).
        m_AllTanks = FindObjectsByType<TankHealth>(FindObjectsSortMode.None);

        // Kick off repeated target-selection to avoid running it every frame.
        StartCoroutine(UpdateTargetRoutine());
    }

    private void Update()
    {
        if (m_CurrentTarget == null)
            return;

        // Tick cooldown timer
        if (m_ShotCooldown > 0f)
            m_ShotCooldown -= Time.deltaTime;

        // Update the NavMesh agent's position so path calculations stay accurate
        m_NavMeshAgent.nextPosition = transform.position;

        switch (m_CurrentState)
        {
            case State.Chase:
                UpdateChase();
                break;

            case State.Attack:
                UpdateAttack();
                break;

            case State.Flee:
                UpdateFlee();
                break;
        }

        // Check whether the target has been stationary for too long
        CheckTargetStationary();
    }

    // -------------------------------------------------------------------------
    // State updates
    // -------------------------------------------------------------------------

    /// <summary>Chase state: follow the path toward the target.</summary>
    private void UpdateChase()
    {
        float targetDistance = Vector3.Distance(transform.position, m_CurrentTarget.position);

        // If we are not charging a shot yet, check if we're close enough to start
        if (targetDistance < m_MaxShootingDistance)
        {
            // Use NavMesh.Raycast to detect obstacles between us and the target.
            // If it returns false there is *no* unobstructed straight-line path,
            // meaning something is blocking the shot – keep chasing instead.
            if (!NavMesh.Raycast(transform.position, m_CurrentTarget.position, out _, ~0))
            {
                // Clear line of sight – switch to Attack
                m_IsMoving = false;
                m_CurrentState = State.Attack;
                return;
            }
        }

        // Not in range (or no clear shot) – keep moving along path
        m_IsMoving = true;
        FollowPath();
    }

    /// <summary>Attack state: stop moving and fire at the target.</summary>
    private void UpdateAttack()
    {
        float targetDistance = Vector3.Distance(transform.position, m_CurrentTarget.position);

        // If target moved out of range, go back to chasing
        if (targetDistance >= m_MaxShootingDistance)
        {
            m_IsMoving = true;
            m_CurrentState = State.Chase;
            return;
        }

        // Check line of sight again – an obstacle may have moved in the way
        if (NavMesh.Raycast(transform.position, m_CurrentTarget.position, out _, ~0))
        {
            // Obstacle in the way – resume chasing
            m_IsMoving = true;
            m_CurrentState = State.Chase;
            return;
        }

        // We aren't charging yet – begin charging if cooldown has expired
        m_IsMoving = false;

        // Face the target while in attack state
        FaceTarget();

        if (m_ShotCooldown <= 0f)
        {
            m_Shooting.StartCharging();

            // When the shot has been charged enough to reach the target, release
            if (m_Shooting.CanHitTarget(targetDistance))
            {
                m_Shooting.Fire();
                m_ShotCooldown = m_ShotCooldownTime;
            }
        }
    }

    /// <summary>Flee state: follow the escape path.</summary>
    private void UpdateFlee()
    {
        m_IsMoving = true;

        if (!FollowPath())
        {
            // Reached the end of the flee path – switch back to chasing
            m_CurrentState = State.Chase;
            RecalculatePath();
        }
    }

    // -------------------------------------------------------------------------
    // Path following helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Steers the tank along m_CurrentPath.corners[].
    /// Returns true while there are still corners left, false when done.
    /// </summary>
    private bool FollowPath()
    {
        if (m_CurrentPath.corners == null || m_CurrentCorner >= m_CurrentPath.corners.Length)
            return false;

        Vector3 nextCorner = m_CurrentPath.corners[m_CurrentCorner];
        Vector3 toCorner = nextCorner - transform.position;
        toCorner.y = 0f;

        // Rotate toward the next corner
        if (toCorner.sqrMagnitude > 0.01f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(toCorner);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, targetRotation,
                GetComponent<TankMovement>().m_TurnSpeed * Time.deltaTime);
        }

        // Move forward
        transform.position += transform.forward * GetComponent<TankMovement>().m_Speed * Time.deltaTime;

        // Check if we've reached the current corner
        if (toCorner.magnitude < 1f)
        {
            m_CurrentCorner++;
            if (m_CurrentCorner >= m_CurrentPath.corners.Length)
                return false;
        }

        return true;
    }

    /// <summary>Smoothly rotate to face the current target.</summary>
    private void FaceTarget()
    {
        Vector3 direction = (m_CurrentTarget.position - transform.position);
        direction.y = 0f;

        if (direction.sqrMagnitude < 0.001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(direction);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, targetRotation,
            GetComponent<TankMovement>().m_TurnSpeed * Time.deltaTime);
    }

    // -------------------------------------------------------------------------
    // Target selection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Coroutine: recalculate the best target every second rather than every frame.
    /// </summary>
    private IEnumerator UpdateTargetRoutine()
    {
        while (true)
        {
            SelectTarget();
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// Calculate a path to every tank and choose the one reachable via the
    /// shortest path. This mirrors the snippet shown in the Unity Learn tutorial.
    /// </summary>
    private void SelectTarget()
    {
        float shortestPath = float.MaxValue;
        int usedPath = -1;
        Transform target = null;

        NavMeshPath[] paths = new NavMeshPath[m_AllTanks.Length];

        // Calculate a path to every tank and check which is closest
        for (int i = 0; i < m_AllTanks.Length; i++)
        {
            var tank = m_AllTanks[i].gameObject;

            // Don't target ourselves
            if (tank == gameObject)
                continue;

            // Skip destroyed or deactivated tanks
            if (tank == null || !tank.activeInHierarchy)
                continue;

            paths[i] = new NavMeshPath();

            // CalculatePath returns true if a valid path was found
            if (NavMesh.CalculatePath(transform.position, tank.transform.position, ~0, paths[i]))
            {
                float length = GetPathLength(paths[i]);

                if (shortestPath > length)
                {
                    usedPath = i;
                    shortestPath = length;
                    target = tank.transform;
                }
            }
        }

        if (target == null)
            return;

        // Store the new target and path
        m_CurrentTarget = target;
        m_LastTargetPosition = m_CurrentTarget.position;
        m_StationaryTimer = 0f;

        m_CurrentPath = paths[usedPath];
        m_CurrentCorner = 1;
    }

    /// <summary>Recalculate a path to the current target (e.g. after fleeing).</summary>
    private void RecalculatePath()
    {
        if (m_CurrentTarget == null)
            return;

        m_CurrentPath = new NavMeshPath();
        m_CurrentCorner = 1;

        NavMesh.CalculatePath(transform.position, m_CurrentTarget.position, ~0, m_CurrentPath);
    }

    // -------------------------------------------------------------------------
    // Flee logic
    // -------------------------------------------------------------------------

    /// <summary>
    /// Check whether the target has been stationary for too long.
    /// If so, assume danger and begin fleeing.
    /// </summary>
    private void CheckTargetStationary()
    {
        if (m_CurrentTarget == null || m_CurrentState == State.Flee)
            return;

        float distanceMoved = Vector3.Distance(m_CurrentTarget.position, m_LastTargetPosition);

        if (distanceMoved < m_StationaryThreshold)
        {
            m_StationaryTimer += Time.deltaTime;

            if (m_StationaryTimer >= m_StationaryCheckTime)
            {
                StartFleeing();
                m_StationaryTimer = 0f;
            }
        }
        else
        {
            m_LastTargetPosition = m_CurrentTarget.position;
            m_StationaryTimer = 0f;
        }
    }

    /// <summary>
    /// Pick a random direction away from the current target and compute an
    /// escape path. Mirrors the snippet shown in the Unity Learn tutorial.
    /// </summary>
    private void StartFleeing()
    {
        // Start by getting the vector *toward* our target…
        var toTarget = (m_CurrentTarget.position - transform.position).normalized;

        // …then rotate that vector by a random angle between 90 and 180 degrees,
        // giving a random direction in the opposite hemisphere.
        toTarget = Quaternion.AngleAxis(
            Random.Range(90.0f, 180.0f) * Mathf.Sign(Random.Range(-1.0f, 1.0f)),
            Vector3.up) * toTarget;

        // Pick a point in that direction at a random distance between 5 and 20 units
        toTarget *= Random.Range(5.0f, 20.0f);

        // Compute a path toward that random point
        if (NavMesh.CalculatePath(transform.position, transform.position + toTarget,
                NavMesh.AllAreas, m_CurrentPath))
        {
            m_CurrentState = State.Flee;
            m_CurrentCorner = 1;
            m_IsMoving = true;
        }
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    /// <summary>Sum the distances between every corner in a NavMesh path.</summary>
    private float GetPathLength(NavMeshPath path)
    {
        float length = 0f;

        if (path.corners.Length < 2)
            return length;

        for (int i = 1; i < path.corners.Length; i++)
            length += Vector3.Distance(path.corners[i - 1], path.corners[i]);

        return length;
    }
}