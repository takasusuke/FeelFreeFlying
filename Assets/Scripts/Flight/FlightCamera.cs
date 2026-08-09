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
        [Tooltip("既定は一人称。試遊の結果、そちらのほうが飛んでいる感じが強いと判断した")]
        [SerializeField] private ViewMode mode = ViewMode.FirstPerson;

        [Tooltip("一人称のとき自分の身体を消す。**残すと視界の中央を塞ぐ**")]
        [SerializeField] private bool hideBodyInFirstPerson = true;

        [Header("三人称")]
        [Tooltip("機体から見た定位置。zが後ろ、yが上。" +
                 "**上げすぎるとマントを見下ろす形になり、身体が隠れる**")]
        [SerializeField] private Vector3 thirdPersonOffset = new Vector3(0f, 0.5f, -4.8f);

        [Tooltip("最高速で追加で後ろに引く距離 (m)")]
        [SerializeField, Range(0f, 20f)] private float speedPullBack = 2.5f;

        [Tooltip("追従の鈍さ (秒)。大きいほど機体が先に動いて速く見える")]
        [SerializeField, Range(0.01f, 1f)] private float followSmoothing = 0.12f;

        [Tooltip("どれだけ前方を見るか (m)")]
        [SerializeField, Min(0f)] private float lookAhead = 12f;

        [Tooltip("ロールをカメラに反映する割合。1にすると地平線が一緒に回って酔う")]
        [SerializeField, Range(0f, 1f)] private float rollInfluence = 0.25f;

        [SerializeField, Range(0.01f, 1f)] private float rotationSmoothing = 0.1f;

        [SerializeField, Min(0.01f)] private float thirdPersonNearClip = 1f;

        [Header("一人称")]
        [Tooltip("飛行中の目の位置（機体ローカル）。身体は消すので、頭より少し前に出す")]
        [SerializeField] private Vector3 firstPersonOffset = new Vector3(0f, 0.16f, 0.75f);

        [Tooltip("歩行中の目の位置。立っている姿勢なので目線が上がる")]
        [SerializeField] private Vector3 walkFirstPersonOffset = new Vector3(0f, 0.72f, 0.12f);

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
        private Renderer[] bodyRenderers;

        /// <summary>いまの視点。HUDの表示に使う。</summary>
        public ViewMode Mode => mode;

        private void Awake()
        {
            cameraComponent = GetComponent<Camera>();
            if (target == null) target = FindFirstObjectByType<FlightController>();
            if (target != null) bodyRenderers = target.GetComponentsInChildren<Renderer>(true);

            ApplyNearClip();
            ApplyBodyVisibility();
            if (target != null) SnapToTarget();
        }

        /// <summary>
        /// 一人称のとき自分の姿を消す。
        ///
        /// 腕が見えるほうが身体で飛んでいる感じが出ると考えて最初は残していたが、
        /// **実際には視界の中央を塞いで街が見えなくなった。** 姿は三人称で見えれば足りる。
        /// </summary>
        private void ApplyBodyVisibility()
        {
            if (bodyRenderers == null) return;

            bool visible = !(hideBodyInFirstPerson && mode == ViewMode.FirstPerson);
            foreach (Renderer renderer in bodyRenderers)
            {
                if (renderer != null) renderer.enabled = visible;
            }
        }

        private void LateUpdate()
        {
            if (target == null) return;

            if (target.ConsumeViewToggle())
            {
                mode = mode == ViewMode.ThirdPerson ? ViewMode.FirstPerson : ViewMode.ThirdPerson;
                ApplyNearClip();
                ApplyBodyVisibility();
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

            // 首振りぶんはbasisに入っているので、機体の前ではなく**見ている方向**を狙う
            Vector3 lookTarget = craft.position + basis * Vector3.forward * lookAhead;
            var desiredRotation = Quaternion.LookRotation(lookTarget - transform.position, basis * Vector3.up);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, desiredRotation, 1f - Mathf.Exp(-dt / rotationSmoothing));
        }

        private void UpdateFirstPerson(float dt)
        {
            Transform craft = target.transform;
            Vector3 desired = craft.TransformPoint(EyeOffset);
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

        /// <summary>目の位置。飛行と歩行で姿勢が違うので分ける。</summary>
        private Vector3 EyeOffset => target.IsWalking ? walkFirstPersonOffset : firstPersonOffset;

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
                    craft.TransformPoint(EyeOffset), Basis(firstPersonRollInfluence));
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
