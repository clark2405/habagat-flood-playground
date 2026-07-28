using UnityEngine;

namespace Habagat
{
    /// <summary>
    /// Orbit control matching the web build's OrbitControls limits.
    ///
    /// The polar clamp is the important one and it is not a preference: the sandbox
    /// is a finite slab sitting in a ring of surrounding land, and letting the camera
    /// drop to the horizon shows it edge-on — you see the underside of the world and
    /// the illusion that the map continues is gone. The web build clamps well above
    /// that, and so does this.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class OrbitCamera : MonoBehaviour
    {
        public Vector3 target = Vector3.zero;
        public float distance = 92f;
        public float minDistance = 25f;
        public float maxDistance = 185f;

        // Degrees from straight down. Never approaches 90, which would put the eye
        // level with the ground.
        public float minPolar = 12f;
        public float maxPolar = 68f;

        public float yaw = 45f;
        public float pitch = 48f;

        public float orbitSpeed = 0.25f;
        public float panSpeed = 0.06f;
        public float zoomSpeed = 8f;

        private void Start() => Apply();

        private void Update()
        {
            // Left drag orbits, right/middle drag pans, wheel zooms.
            if (Input.GetMouseButton(0))
            {
                yaw += Input.GetAxis("Mouse X") * orbitSpeed * 12f;
                pitch -= Input.GetAxis("Mouse Y") * orbitSpeed * 12f;
            }
            else if (Input.GetMouseButton(1) || Input.GetMouseButton(2))
            {
                // Pan across the ground plane, not the screen plane, so dragging
                // tracks the terrain under the cursor rather than sliding the world.
                var right = transform.right;
                var fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                target -= (right * Input.GetAxis("Mouse X") + fwd * Input.GetAxis("Mouse Y"))
                          * panSpeed * distance;
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f) distance -= scroll * zoomSpeed * distance * 0.1f;

            Apply();
        }

        private void Apply()
        {
            distance = Mathf.Clamp(distance, minDistance, maxDistance);
            pitch = Mathf.Clamp(pitch, minPolar, maxPolar);

            // Spherical to Cartesian, with pitch measured from vertical.
            float p = pitch * Mathf.Deg2Rad, y = yaw * Mathf.Deg2Rad;
            var offset = new Vector3(
                Mathf.Sin(p) * Mathf.Sin(y),
                Mathf.Cos(p),
                Mathf.Sin(p) * Mathf.Cos(y)) * distance;

            transform.position = target + offset;
            transform.LookAt(target);
        }
    }
}
