using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 飛行カメラ。三人称と一人称を切り替えられる。
    ///
    /// 要件（`requirements.md` §9）は三人称を基本としているが、**一人称のほうが
    /// 「自分が飛んでいる」感じが強い**という判断もありうる。M1はそこを決める工程なので、
    /// 両方を用意して試遊で選ぶ。**選んだら要件側を直す。**
    ///
    /// 三人称では機体の回転をそのまま追わない。ロールのたびに地平線が回って酔うため、
    /// ロールは一部しか拾わない。一人称は逆に、拾わなすぎると首だけ動かない人形のようになるので、
    /// 比率を別に持つ。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class FlightCamera : MonoBehaviour
    {
        public enum ViewMode
        {
            ThirdPerson,
            FirstPerson,
        }

        [Header("参照")]
        [SerializeField] private FlightController target;

        [Header("視点")]
        [SerializeField] private ViewMode mode = ViewMode.ThirdPerson;

        [Header("三人称")]
        [Tooltip("機体から見た定位置。zが後ろ、yが上")]
        [SerializeField] private Vector3 thirdPersonOffset = new Vector3(0f, 1.7f, -6f);

        [Tooltip("最高速で追加で後ろに引く距離 (m)")]
        [SerializeField, Range(0f, 20f)] private float speedPullBack = 3.5f;

        [Tooltip("追従の鈍さ (秒)。大きいほど機体が先に動いて速く見える")]
        [SerializeField, Range(0.01f, 1f)] private float followSmoothing = 0.12f;

        [Tooltip("どれだけ前方を見るか (m)")]
        [SerializeField, Min(0f)] private float lookAhead = 12f;

        [Tooltip("ロールをカメラに反映する割合。1にすると地平線が一緒に回って酔う")]
        [SerializeField, Range(0f, 1f)] private float rollInfluence = 0.25f;

        [SerializeField, Range(0.01f, 1f)] private float rotationSmoothing = 0.1f;

        [SerializeField, Min(0.01f)] private float thirdPersonNearClip = 1f;

        [Header("一人称")]
        [Tooltip("頭の位置（機体ローカル）。前に出しすぎると自分の腕が見えなくなる")]
        [SerializeField] private Vector3 firstPersonOffset = new Vector3(0f, 0.14f, 0.62f);

        [Tooltip("一人称でのロールの反映。三人称より強めでないと身体と視界がずれる")]
        [SerializeField, Range(0f, 1f)] private float firstPersonRollInfluence = 0.7f;

        [Tooltip("一人称の追従の鈍さ (秒)。**小さくする。** 遅れると強く酔う")]
        [SerializeField, Range(0f, 0.3f)] private float firstPersonSmoothing = 0.03f;

        [Tooltip("腕が映るように近くまで描く。三人称の1mのままだと腕が切れる")]
        [SerializeField, Min(0.01f)] private float firstPersonNearClip = 0.12f;

        [Header("画角")]
        [SerializeField, Range(30f, 120f)] private float fieldOfViewMin = 62f;
        [SerializeField, Range(30f, 130f)] private float fieldOfViewMax = 82f;
        [SerializeField, Range(0f, 20f)] private float boostFieldOfViewBonus = 6f;
        [SerializeField, Range(0.01f, 1f)] private float fieldOfViewSmoothing = 0.25f;

        private Camera cameraComponent;
        private Vector3 followVelocity;

        /// <summary>いまの視点。HUDの表示に使う。</summary>
        public ViewMode Mode => mode;

        private void Awake()
        {
            cameraComponent = GetComponent<Camera>();
            if (target == null) target = FindFirstObjectByType<FlightController>();
            ApplyNearClip();
            if (target != null) SnapToTarget();
        }

        private void LateUpdate()
        {
            if (target == null) return;

            if (target.ConsumeViewToggle())
            {
                mode = mode == ViewMode.ThirdPerson ? ViewMode.FirstPerson : ViewMode.ThirdPerson;
                ApplyNearClip();
                SnapToTarget();
            }

            float dt = Time.deltaTime;
            if (mode == ViewMode.FirstPerson) UpdateFirstPerson(dt); else UpdateThirdPerson(dt);
            UpdateFieldOfView(dt);
        }

        private void UpdateThirdPerson(float dt)
        {
            Transform craft = target.transform;
            Quaternion basis = Basis(rollInfluence);

            Vector3 desired = craft.position +
                              basis * (thirdPersonOffset + Vector3.back * (speedPullBack * target.SpeedRatio));

            transform.position = Vector3.SmoothDamp(
                transform.position, desired, ref followVelocity, followSmoothing);

            Vector3 lookTarget = craft.position + craft.forward * lookAhead;
            var desiredRotation = Quaternion.LookRotation(lookTarget - transform.position, basis * Vector3.up);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, desiredRotation, 1f - Mathf.Exp(-dt / rotationSmoothing));
        }

        private void UpdateFirstPerson(float dt)
        {
            Transform craft = target.transform;
            Vector3 desired = craft.TransformPoint(firstPersonOffset);
            Quaternion desiredRotation = Basis(firstPersonRollInfluence);

            if (firstPersonSmoothing <= 0f)
            {
                transform.SetPositionAndRotation(desired, desiredRotation);
                return;
            }

            float t = 1f - Mathf.Exp(-dt / firstPersonSmoothing);
            transform.position = Vector3.Lerp(transform.position, desired, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, t);
        }

        /// <summary>ヨーとピッチはそのまま、ロールだけ割合で薄めた姿勢。</summary>
        private Quaternion Basis(float roll)
        {
            Vector3 euler = target.transform.eulerAngles;
            return Quaternion.Euler(euler.x, euler.y, -target.RollDegrees * roll);
        }

        private void UpdateFieldOfView(float dt)
        {
            float desired = Mathf.Lerp(fieldOfViewMin, fieldOfViewMax, target.SpeedRatio) +
                            (target.IsBoosting ? boostFieldOfViewBonus : 0f);
            cameraComponent.fieldOfView = Mathf.Lerp(
                cameraComponent.fieldOfView, desired, 1f - Mathf.Exp(-dt / fieldOfViewSmoothing));
        }

        private void ApplyNearClip()
        {
            if (cameraComponent == null) cameraComponent = GetComponent<Camera>();
            cameraComponent.nearClipPlane =
                mode == ViewMode.FirstPerson ? firstPersonNearClip : thirdPersonNearClip;
        }

        /// <summary>補間を挟まずに定位置へ。開始時・リセット時・視点切替時に使う。</summary>
        public void SnapToTarget()
        {
            Transform craft = target.transform;

            if (mode == ViewMode.FirstPerson)
            {
                transform.SetPositionAndRotation(
                    craft.TransformPoint(firstPersonOffset), Basis(firstPersonRollInfluence));
            }
            else
            {
                transform.position = craft.position + craft.rotation * thirdPersonOffset;
                transform.rotation = Quaternion.LookRotation(
                    craft.position + craft.forward * lookAhead - transform.position, Vector3.up);
            }

            followVelocity = Vector3.zero;
        }
    }
}
