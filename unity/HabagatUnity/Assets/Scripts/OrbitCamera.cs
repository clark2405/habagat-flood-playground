using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

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

        /// <summary>
        /// 135, not 45. The reference camera sits at (56, 52, 56) in a right-handed
        /// scene, which mirrors to (56, 52, -56) here — positive X, NEGATIVE Z. A yaw
        /// of 45 puts the eye at +X +Z, the opposite corner, and since prop geometry
        /// was mirrored to face the intended camera every building would then present
        /// its back: porticos, doors and windows all on the hidden side.
        /// </summary>
        public float yaw = 135f;
        public float pitch = 48f;

        /// <summary>
        /// Cleared while a paint tool is active. The camera and the brush both want
        /// the left drag, so exactly one of them may have it — which is why the
        /// reference makes "Pan &amp; Orbit" a tool rather than a mode toggle.
        /// </summary>
        [System.NonSerialized] public bool orbitEnabled = true;

        public float orbitSpeed = 0.25f;
        public float panSpeed = 0.06f;
        public float zoomSpeed = 8f;

        private void Start() => Apply();

        /// <summary>Gap between the two fingers last frame, for the pinch.</summary>
        private float _lastPinch;

        private void Update()
        {
            if (TouchUpdate()) { Apply(); return; }

            var mouse = Mouse.current;
            if (mouse == null) return;

            // Raw pixel delta, scaled to roughly what the old Input.GetAxis("Mouse X")
            // used to return, so the tuned speeds below still mean what they say.
            Vector2 d = mouse.delta.ReadValue() * 0.1f;

            // Left drag orbits, right/middle drag pans, wheel zooms.
            if (orbitEnabled && mouse.leftButton.isPressed)
            {
                yaw += d.x * orbitSpeed * 12f;
                pitch -= d.y * orbitSpeed * 12f;
            }
            else if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
            {
                // Pan across the ground plane, not the screen plane, so dragging
                // tracks the terrain under the cursor rather than sliding the world.
                var right = transform.right;
                var fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                target -= (right * d.x + fwd * d.y) * panSpeed * distance;
            }

            // One wheel notch is ~120 here, against ~0.1 from the old axis.
            float scroll = mouse.scroll.ReadValue().y / 1200f;
            if (Mathf.Abs(scroll) > 0.0001f) distance -= scroll * zoomSpeed * distance * 0.1f;

            Apply();
        }

        /// <summary>
        /// Touch: one finger orbits, two pan and pinch. Returns whether it took the
        /// frame, so the mouse path is skipped rather than adding to it — a Windows
        /// laptop has both devices and would otherwise apply each gesture twice.
        ///
        /// Deltas are normalised to a 1080-pixel screen instead of used raw. Raw
        /// pixels make the same physical swipe mean wildly different things across
        /// devices: a full-height drag is 1080 px on a monitor and over 2000 on a
        /// dense phone panel, so a gesture tuned on one is half as fast on the other.
        /// Against screen height, a drag across the same FRACTION of the glass turns
        /// the camera the same amount everywhere, and the tuned speeds below keep
        /// meaning what they said when they were set with a mouse.
        /// </summary>
        private bool TouchUpdate()
        {
            var ts = Touchscreen.current;
            if (ts == null) return false;

            TouchControl a = null, b = null;
            foreach (var t in ts.touches)
            {
                if (!t.press.isPressed) continue;
                if (a == null) a = t;
                else if (b == null) { b = t; break; }
            }
            if (a == null) { _lastPinch = 0f; return false; }

            float scale = 0.1f * 1080f / Mathf.Max(Screen.height, 1);

            if (b == null)
            {
                _lastPinch = 0f;
                // A single finger is the one gesture the brush also wants, so it obeys
                // the same rule the left mouse button does.
                if (orbitEnabled)
                {
                    Vector2 d = a.delta.ReadValue() * scale;
                    yaw += d.x * orbitSpeed * 12f;
                    pitch -= d.y * orbitSpeed * 12f;
                }
                return true;
            }

            // Two fingers pan and zoom whatever the active tool is: a second finger
            // cannot be part of a paint stroke, so there is nothing to arbitrate.
            Vector2 pa = a.position.ReadValue(), pb = b.position.ReadValue();
            float pinch = Vector2.Distance(pa, pb);
            if (_lastPinch > 1f && pinch > 1f) distance *= _lastPinch / pinch;
            _lastPinch = pinch;

            Vector2 drag = (a.delta.ReadValue() + b.delta.ReadValue()) * 0.5f * scale;
            var right = transform.right;
            var fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            target -= (right * drag.x + fwd * drag.y) * panSpeed * distance;
            return true;
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
