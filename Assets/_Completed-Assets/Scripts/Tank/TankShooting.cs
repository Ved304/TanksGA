using UnityEngine;
using UnityEngine.UI;

namespace Complete
{
    public class TankShooting : MonoBehaviour
    {
        public int m_PlayerNumber = 1;              // Used to identify the different players.
        public Rigidbody m_Shell;                   // Prefab of the shell.
        public Transform m_FireTransform;           // A child of the tank where the shells are spawned.
        public Slider m_AimSlider;                  // A child of the tank that displays the current launch force.
        public AudioSource m_ShootingAudio;         // Reference to the audio source used to play the shooting audio. NB: different to the movement audio source.
        public AudioClip m_ChargingClip;            // Audio that plays when each shot is charging up.
        public AudioClip m_FireClip;                // Audio that plays when each shot is fired.
        public float m_MinLaunchForce = 15f;        // The force given to the shell if the fire button is not held.
        public float m_MaxLaunchForce = 30f;        // The force given to the shell if the fire button is held for the max charge time.
        public float m_MaxChargeTime = 0.75f;       // How long the shell can charge for before it is fired at max force.


        private string m_FireButton;                // The input axis that is used for launching shells.
        private float m_CurrentLaunchForce;         // The force that will be given to the shell when the fire button is released.
        private float m_ChargeSpeed;                // How fast the launch force increases, based on the max charge time.
        private bool m_Fired;                       // Whether or not the shell has been launched with this button press.
        private bool m_IsComputerControlled;        // Set by TankMovement at startup; suppresses player input when true.
        private bool m_IsCharging;                  // True while the AI is actively charging a shot.


        private void OnEnable()
        {
            // When the tank is turned on, reset the launch force and the UI.
            m_CurrentLaunchForce = m_MinLaunchForce;
            m_AimSlider.value = m_MinLaunchForce;
        }


        private void Start()
        {
            // The fire axis is based on the player number.
            m_FireButton = "Fire" + m_PlayerNumber;

            // The rate that the launch force charges up is the range of possible forces by the max charge time.
            m_ChargeSpeed = (m_MaxLaunchForce - m_MinLaunchForce) / m_MaxChargeTime;
        }


        /// <summary>
        /// Called by TankMovement (or GameManager) to tell this component
        /// whether it is driven by AI or by a human player.
        /// </summary>
        public void SetComputerControlled(bool isComputerControlled)
        {
            m_IsComputerControlled = isComputerControlled;
        }


        private void Update()
        {
            // The slider should have a default value of the minimum launch force.
            m_AimSlider.value = m_MinLaunchForce;

            if (m_IsComputerControlled)
            {
                // AI input is handled via StartCharging() / Fire() public methods.
                // We still need to auto-fire if the charge reaches maximum,
                // and update the aim slider while charging.
                if (m_IsCharging)
                {
                    if (m_CurrentLaunchForce >= m_MaxLaunchForce && !m_Fired)
                    {
                        m_CurrentLaunchForce = m_MaxLaunchForce;
                        Fire();
                    }
                    else if (!m_Fired)
                    {
                        m_CurrentLaunchForce += m_ChargeSpeed * Time.deltaTime;
                        m_AimSlider.value = m_CurrentLaunchForce;
                    }
                }
                return; // Skip all player-input handling below.
            }

            // ---- Player-controlled path (unchanged) ----

            // If the max force has been exceeded and the shell hasn't yet been launched...
            if (m_CurrentLaunchForce >= m_MaxLaunchForce && !m_Fired)
            {
                // ... use the max force and launch the shell.
                m_CurrentLaunchForce = m_MaxLaunchForce;
                Fire();
            }
            // Otherwise, if the fire button has just started being pressed...
            else if (Input.GetButtonDown(m_FireButton))
            {
                // ... reset the fired flag and reset the launch force.
                m_Fired = false;
                m_CurrentLaunchForce = m_MinLaunchForce;

                // Change the clip to the charging clip and start it playing.
                m_ShootingAudio.clip = m_ChargingClip;
                m_ShootingAudio.Play();
            }
            // Otherwise, if the fire button is being held and the shell hasn't been launched yet...
            else if (Input.GetButton(m_FireButton) && !m_Fired)
            {
                // Increment the launch force and update the slider.
                m_CurrentLaunchForce += m_ChargeSpeed * Time.deltaTime;
                m_AimSlider.value = m_CurrentLaunchForce;
            }
            // Otherwise, if the fire button is released and the shell hasn't been launched yet...
            else if (Input.GetButtonUp(m_FireButton) && !m_Fired)
            {
                // ... launch the shell.
                Fire();
            }
        }


        // -------------------------------------------------------------------------
        // Public AI interface
        // -------------------------------------------------------------------------

        /// <summary>
        /// Called by TankAI to begin charging a shot.
        /// Has no effect if a shot is already being charged or was just fired.
        /// </summary>
        public void StartCharging()
        {
            if (m_IsCharging || m_Fired)
                return;

            m_Fired = false;
            m_IsCharging = true;
            m_CurrentLaunchForce = m_MinLaunchForce;

            m_ShootingAudio.clip = m_ChargingClip;
            m_ShootingAudio.Play();
        }

        /// <summary>
        /// Returns true when the current launch force is sufficient to reach
        /// <paramref name="targetDistance"/> units. TankAI uses this to decide
        /// when to release the shot.
        /// </summary>
        public bool CanHitTarget(float targetDistance)
        {
            // Simple ballistic estimate: range ≈ v² / g  (flat ground, 45° launch).
            // FireTransform typically pitches slightly upward; this gives a good
            // enough approximation without needing the exact launch angle.
            float estimatedRange = (m_CurrentLaunchForce * m_CurrentLaunchForce) / Physics.gravity.magnitude;
            return estimatedRange >= targetDistance;
        }

        /// <summary>
        /// Returns the charge ratio [0, 1] so TankAI can read how charged
        /// the current shot is (0 = min force, 1 = max force).
        /// </summary>
        public float GetChargeRatio()
        {
            return Mathf.InverseLerp(m_MinLaunchForce, m_MaxLaunchForce, m_CurrentLaunchForce);
        }


        // -------------------------------------------------------------------------
        // Firing (private – called internally by both player and AI paths)
        // -------------------------------------------------------------------------

        /// <summary>
        /// Instantiates a shell and launches it. Can also be called publicly
        /// by TankAI when it wants to release a charged shot immediately.
        /// </summary>
        public void Fire()
        {
            // Set the fired flag so Fire is only called once per charge.
            m_Fired = true;
            m_IsCharging = false;

            // Create an instance of the shell and store a reference to its rigidbody.
            Rigidbody shellInstance =
                Instantiate(m_Shell, m_FireTransform.position, m_FireTransform.rotation) as Rigidbody;

            // Set the shell's velocity to the launch force in the fire position's forward direction.
            shellInstance.velocity = m_CurrentLaunchForce * m_FireTransform.forward;

            // Change the clip to the firing clip and play it.
            m_ShootingAudio.clip = m_FireClip;
            m_ShootingAudio.Play();

            // Reset the launch force. This is a precaution in case of missing button events.
            m_CurrentLaunchForce = m_MinLaunchForce;
        }
    }
}