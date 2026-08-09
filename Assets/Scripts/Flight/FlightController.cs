using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 飛行と歩行（M1の本体 → `docs/m1-plan.md`）。
    ///
    /// **航空力学は再現しない**（`CLAUDE.md` 不変条件3）。失速・迎え角・エンジン出力を持たず、
    /// 姿勢角を直接積分する。落下も墜落も無い。速度の下限が0より上なので、**操作を止めても
    /// 滑空し続ける**——これが「浮遊感」の土台になる。
    ///
    /// 唯一物理っぽく振る舞うのが<see cref="diveAcceleration"/>で、機首を下げると速度が乗り、
    /// 上げると落ちる。これが無いと、どの姿勢でも同じ速度で飛ぶ「レール感」が出る。
    ///
    /// 飛行中は当たり判定を持たない（建物をすり抜ける）。**歩行中だけ**
    /// <see cref="CharacterController"/> を有効にして屋上に立つ。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlightController : MonoBehaviour
    {
        public enum MotionMode
        {
            Flying,
            Walking,
        }

        [Header("参照")]
        [SerializeField] private FlightInput input;

        [Header("速度 (m/s)")]
        [Tooltip("スロットルを絞りきった時の速度。**0にしない**。止まると浮遊感が消える")]
        [SerializeField, Min(0.5f)] private float speedMin = 6f;

        [SerializeField, Min(1f)] private float speedMax = 150f;

        [Tooltip("開始時および姿勢リセット時の速度")]
        [SerializeField, Min(1f)] private float speedStart = 22f;

        [Tooltip("W/Sを押している間に速度が変わる割合 (m/s^2)。**離した速度をそのまま保つ**")]
        [SerializeField, Min(1f)] private float throttleAcceleration = 18f;

        [Tooltip("ブースト中の速度倍率")]
        [SerializeField, Range(1f, 3f)] private float boostMultiplier = 1.7f;

        [Header("姿勢")]
        [Tooltip("機首上下の角速度 (度/秒)")]
        [SerializeField, Min(1f)] private float pitchRate = 55f;

        [Tooltip("**真上・真下まで向ける。** 65度で止めると天頂と真下へ抜けられない")]
        [SerializeField, Range(10f, 89f)] private float pitchLimit = 88f;

        [Tooltip("ロールの角速度 (度/秒)")]
        [SerializeField, Min(1f)] private float rollRate = 120f;

        [SerializeField, Range(10f, 89f)] private float rollLimit = 70f;

        [Tooltip("姿勢の追従の鈍さ (秒)。大きいほどふわっとするが、遅れて感じる")]
        [SerializeField, Range(0f, 0.5f)] private float attitudeSmoothing = 0.1f;

        [Tooltip("最高速での旋回の効き。1で速度に関係なく同じ、小さいほど高速では曲がらない。" +
                 "**0.35で試したところ街中が窮屈になったので1に戻した**（急旋回にブレーキを要求する設計は不採用）")]
        [SerializeField, Range(0.1f, 1f)] private float highSpeedTurnFactor = 1f;

        [Header("浮遊感")]
        [Tooltip("機首を下げた時に乗る加速度 (m/s^2)。0で無効")]
        [SerializeField, Range(0f, 60f)] private float diveAcceleration = 14f;

        [Tooltip("上昇中に削がれる減速度 (m/s^2)。**既定は0。** " +
                 "入れると上昇しながら加速を止めた時に速度が落ち続け、「離した速度を保つ」が成立しない")]
        [SerializeField, Range(0f, 60f)] private float climbDeceleration = 0f;

        [Header("高度")]
        [Tooltip("海面の高さ。シーン生成時に街の最下点から決めて入れる（M1SceneSetup）")]
        [SerializeField] private float seaLevel = 0f;

        [Tooltip("海面からこれ以上は下がれない。**海に沈まないための最後の受け皿**")]
        [SerializeField, Min(0f)] private float altitudeAboveSeaMin = 3f;

        [SerializeField] private float altitudeMax = 2000f;

        [Header("当たり判定")]
        [Tooltip("飛行中も建物にぶつかる。**負荷はほぼ無い**（コライダーは地域単位で9個、" +
                 "判定はカプセル1個ぶん）。問題は負荷ではなく、ぶつかった時の気持ちよさ")]
        [SerializeField] private bool collideWhileFlying = true;

        [Tooltip("接触した時に速度に掛ける係数。1で減速しない。**止めない**——" +
                 "急停止は墜落と同じ体験になる（CLAUDE.md 不変条件3）")]
        [SerializeField, Range(0.1f, 1f)] private float grazeSpeedFactor = 0.6f;

        [Header("歩行")]
        [Tooltip("L2を踏んだ時の減速 (m/s^2)。**急ブレーキ**。屋上に降りる時に要る")]
        [SerializeField, Min(1f)] private float brakeAcceleration = 70f;

        [Tooltip("接地している時に入力へ追従する速さ (m/s^2)。**小さいほど着地後に滑る**")]
        [SerializeField, Min(1f)] private float groundAcceleration = 40f;

        [SerializeField, Min(0.5f)] private float walkSpeed = 4.5f;

        [Tooltip("ダッシュ。屋上から屋上へ助走をつけて跳べる速さにしてある")]
        [SerializeField, Min(0.5f)] private float runSpeed = 11f;

        [SerializeField, Min(1f)] private float jumpSpeed = 7f;
        [SerializeField, Min(1f)] private float gravity = 22f;

        [Header("パルクール")]
        [Tooltip("空中でもう一度跳べる回数")]
        [SerializeField, Min(0)] private int airJumpsMax = 1;

        [SerializeField, Min(1f)] private float airJumpSpeed = 6f;

        [Tooltip("壁に触れながらジャンプを押し続けると駆け上がる速さ (m/s)")]
        [SerializeField, Min(0f)] private float wallRunSpeed = 7f;

        [Tooltip("駆け上がれる時間 (秒)。これを過ぎたら落ちる")]
        [SerializeField, Min(0f)] private float wallRunSeconds = 1.1f;

        [Tooltip("歩行時の視線の速さ (度/秒)")]
        [SerializeField, Min(1f)] private float lookRate = 130f;

        [Tooltip("飛び立つ時の初速 (m/s)")]
        [SerializeField, Min(1f)] private float launchSpeed = 16f;

        [Tooltip("この角度より上を向いてダッシュすると、そのまま飛行へ移る (度)")]
        [SerializeField, Range(5f, 80f)] private float seamlessLaunchPitch = 30f;

        [Tooltip("飛び立った直後に上向き入力を捨てる時間 (秒)")]
        [SerializeField, Range(0f, 3f)] private float climbLockSeconds = 0.8f;

        [Tooltip("落下中に左スティックで動ける割合。1で地上と同じ")]
        [SerializeField, Range(0f, 1f)] private float airControl = 1f;

        [Tooltip("空中で進行方向を変えられる速さ (m/s^2)。**小さいほど慣性が強い**")]
        [SerializeField, Min(1f)] private float airAcceleration = 22f;

        [Tooltip("視線追従の時、横移動が進路を曲げる強さ")]
        [SerializeField, Range(0f, 2f)] private float StrafeInfluence = 0.7f;

        private float pitchDegrees;
        private float rollDegrees;
        private float yawDegrees;
        private float speed;
        private float verticalVelocity;
        private int airJumpsUsed;
        private float wallRunRemaining;
        private bool jumpHeldLastFrame;
        private bool recenterViewRequested;
        private float climbLockRemaining;
        private Vector3 lastHitNormal;
        private Vector3 walkVelocity;

        private Vector3 startPosition;
        private float startYaw;

        private CharacterController body;
        private bool viewToggleRequested;

        /// <summary>現在の速度 (m/s)。HUDとカメラが読む。</summary>
        public float Speed => speed;

        /// <summary>速度の範囲内での位置 (0〜1)。カメラの画角に使う。</summary>
        public float SpeedRatio => Mathf.InverseLerp(speedMin, speedMax, speed);

        public float PitchDegrees => pitchDegrees;
        public float RollDegrees => rollDegrees;
        public bool IsBoosting { get; private set; }

        /// <summary>視点を動かす入力（右スティック）。カメラが読む。**進路には影響しない。**</summary>
        public Vector2 LookInput { get; private set; }

        /// <summary>横移動の入力。視線追従の時だけ使う（左スティックの左右）。</summary>
        private float StrafeInput { get; set; }

        /// <summary>左スティックの上下による加減速。視線追従の時だけ使う。</summary>
        private float ThrottleFromStick { get; set; }

        /// <summary>視点を進行方向へ戻す指示があったか。カメラが読んで消費する。</summary>
        public bool ConsumeRecenterView()
        {
            if (!recenterViewRequested) return false;
            recenterViewRequested = false;
            return true;
        }
        public MotionMode Mode { get; private set; } = MotionMode.Flying;
        public bool IsWalking => Mode == MotionMode.Walking;
        public bool InvertPitch => FlightSettings.InvertPitch;

        /// <summary>着地に失敗した等、HUDに1行出したいときのメッセージ。</summary>
        public string Notice { get; private set; }
        public float NoticeTime { get; private set; }

        private void Awake()
        {
            if (input == null) input = GetComponent<FlightInput>();
            body = GetComponent<CharacterController>();
            if (body != null) body.enabled = collideWhileFlying;

            startPosition = transform.position;
            startYaw = transform.eulerAngles.y;
            ResetPose();
        }

        private void Update()
        {
            FlightInputState state = input != null ? input.Read() : default;
            float dt = Time.deltaTime;

            if (state.ToggleView) viewToggleRequested = true;
            if (state.RecenterView) recenterViewRequested = true;
            if (state.ToggleInvert)
            {
                FlightSettings.InvertPitch = !FlightSettings.InvertPitch;
                ShowNotice(FlightSettings.InvertPitch ? "上下反転: あり" : "上下反転: なし");
            }

            if (state.ToggleCollision)
            {
                collideWhileFlying = !collideWhileFlying;
                ShowNotice(collideWhileFlying ? "飛行中の当たり判定: あり" : "飛行中の当たり判定: なし（すり抜け）");
            }

            if (state.Reset)
            {
                ResetPose();
                return;
            }

            if (state.ToggleMotion)
            {
                if (Mode == MotionMode.Flying) StopFlying(); else Launch();
            }

            if (Mode == MotionMode.Flying)
            {
                UpdateAttitude(state, dt);
                UpdateSpeed(state, dt);
                MoveFlying(dt);
            }
            else
            {
                UpdateWalking(state, dt);
            }
        }

        // ------------------------------------------------------------------ 飛行

        /// <summary>
        /// 姿勢。**進む方向は左スティック（またはマウス）で決める。**
        /// 左右で旋回、上下で上昇・下降。右スティックは<see cref="LookInput"/>としてカメラへ渡し、
        /// **進路には一切影響しない**。
        ///
        /// 一度は「見ている方向へ飛ぶ」にしたが、それだと**高度を保ったまま下を眺める**ことが
        /// できない。街を見に来ている以上、見る方向と進む方向は別々に操作できる必要がある。
        /// </summary>
        private void UpdateAttitude(FlightInputState state, float dt)
        {
            bool followView = FlightSettings.Steering == SteeringMode.FollowView;

            // 視線追従（スパイダーマン式）では右スティックが進路そのものになる。
            // 独立視点では左スティックが進路で、右スティックは視点だけ動かす
            Vector2 steer = followView ? state.RightStick : state.LeftStick;
            StrafeInput = followView ? Mathf.Clamp(state.LeftStick.x + state.Keys.x, -1f, 1f) : 0f;
            ThrottleFromStick = followView ? state.LeftStick.y : 0f;

            // 反転の対象はマウスとスティックだけ（矢印キーは常に↑で上向き）
            float turnInput = Mathf.Clamp(state.Aim.x + steer.x + (followView ? 0f : state.Keys.x), -1f, 1f);
            float climbInput = Mathf.Clamp(
                (state.Aim.y + steer.y) * (FlightSettings.InvertPitch ? 1f : -1f) + state.Arrows.y, -1f, 1f);

            // 飛び立った直後だけ、上向きの入力を無視する（走り出しの前傾がそのまま上昇に化けるため）
            if (climbLockRemaining > 0f)
            {
                climbLockRemaining -= dt;
                if (climbInput > 0f) climbInput = 0f;
            }

            // **速いほど曲がらない。** 急旋回したければ減速する、という関係を作る。
            // これがないと、速度は「景色の流れる速さ」でしかなくなり、
            // ブレーキ（L2）を使う理由が着地の時だけになる
            float agility = Mathf.Lerp(1f, highSpeedTurnFactor, SpeedRatio);

            yawDegrees += turnInput * lookRate * agility * dt;
            pitchDegrees = Mathf.Clamp(
                pitchDegrees + climbInput * pitchRate * agility * dt, -pitchLimit, pitchLimit);

            // 視線追従では視点＝進路なので、カメラを別に振らない
            LookInput = followView ? Vector2.zero : state.RightStick;

            if (state.LevelFlight)
            {
                pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, pitchRate * 2f * dt);
            }

            // ロールは見た目だけ。曲がっている方向へ機体が傾く
            float targetRoll = turnInput * rollLimit;
            rollDegrees = Mathf.MoveTowards(rollDegrees, targetRoll, rollRate * dt);

            var target = Quaternion.Euler(-pitchDegrees, yawDegrees, -rollDegrees);
            transform.rotation = attitudeSmoothing > 0f
                ? Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-dt / attitudeSmoothing))
                : target;
        }

        /// <summary>
        /// 速度。**スロットルは「目標速度」ではなく「増減」**として扱う。
        ///
        /// 押している間だけ速度が変わり、離した速度をそのまま保つ。目標値へ戻る作りだと、
        /// 手を離すたびに巡航速度へ引き戻され、「この速さで流したい」が効かない。
        ///
        /// ブーストは<see cref="speed"/>を書き換えず、移動時に掛けるだけにする。
        /// 書き換えると、離した瞬間の速い値が巡航速度として居座ってしまう。
        /// </summary>
        private void UpdateSpeed(FlightInputState state, float dt)
        {
            IsBoosting = state.Boost;

            float throttleInput = state.Trigger + state.Keys.y + ThrottleFromStick;
            speed += Mathf.Clamp(throttleInput, -1f, 1f) * throttleAcceleration * dt;

            // L2は普通の減速ではなく**急ブレーキ**にする。狙った屋上へ降りるには、
            // 速度を素早く殺せる操作が要る
            speed -= state.TriggerLeft * brakeAcceleration * dt;

            // 降下で速度が乗る。上昇では**削がない**のが既定——加速を止めたら
            // その速度を保つ、が守れなくなるため（climbDecelerationで戻せる）
            float slope = Mathf.Sin(-pitchDegrees * Mathf.Deg2Rad);
            speed += slope * (slope > 0f ? diveAcceleration : climbDeceleration) * dt;

            speed = Mathf.Clamp(speed, speedMin, speedMax);
        }

        /// <summary>実際に進む速さ。ブーストは掛けるだけで、巡航速度は変えない。</summary>
        private float EffectiveSpeed => IsBoosting ? speed * boostMultiplier : speed;

        private void MoveFlying(float dt)
        {
            // 視線追従では左スティックの左右で進路を横へずらす（旋回は右スティック側）
            Vector3 direction = transform.forward + transform.right * (StrafeInput * StrafeInfluence);
            Vector3 delta = direction.normalized * (EffectiveSpeed * dt);

            if (collideWhileFlying && body != null)
            {
                if (!body.enabled) body.enabled = true;
                lastHitNormal = Vector3.zero;
                body.Move(delta);

                if (body.collisionFlags != CollisionFlags.None) HandleGraze(dt);
            }
            else
            {
                if (body != null && body.enabled) body.enabled = false;
                transform.position += delta;
            }

            ClampAltitude(dt);
        }

        /// <summary>
        /// 壁に当たった時の処理。**浅く掠めたなら、少し減速して壁沿いに流す。**
        ///
        /// 当たり方に関係なく一律で減速すると、ビルを掠めただけで最低速度まで落ちてしまい、
        /// 街の間を縫って飛ぶことが罰になる。正面衝突（法線と真っ向）だけ強く落とす。
        /// 進路も壁面に沿わせる——機首が壁を向いたままだと、擦りながら止まり続ける。
        /// </summary>
        private void HandleGraze(float dt)
        {
            if (lastHitNormal == Vector3.zero)
            {
                speed = Mathf.Max(speedMin, speed * grazeSpeedFactor);
                return;
            }

            // 0 = 平行に掠めた、1 = 正面から突っ込んだ
            float headOn = Mathf.Clamp01(Vector3.Dot(transform.forward, -lastHitNormal));

            float damping = Mathf.Lerp(1f, grazeSpeedFactor, headOn);
            speed = Mathf.Max(speedMin, speed * Mathf.Pow(damping, dt * 60f));

            // 壁面へ投影した向きへ機首を寄せる
            Vector3 along = Vector3.ProjectOnPlane(transform.forward, lastHitNormal);
            if (along.sqrMagnitude < 0.0001f) return;

            along.Normalize();
            yawDegrees = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
            pitchDegrees = Mathf.Clamp(Mathf.Asin(along.y) * Mathf.Rad2Deg, -pitchLimit, pitchLimit);
        }

        /// <summary>当たった面の向きを覚えておく。<see cref="HandleGraze"/>が使う。</summary>
        private void OnControllerColliderHit(ControllerColliderHit hit) => lastHitNormal = hit.normal;

        /// <summary>海に沈まない・上がりすぎない。地面や建物は当たり判定に任せる。</summary>
        private void ClampAltitude(float dt)
        {
            Vector3 position = transform.position;
            float floor = seaLevel + altitudeAboveSeaMin;

            if (position.y < floor)
            {
                position.y = floor;
                if (pitchDegrees < 0f) pitchDegrees = Mathf.MoveTowards(pitchDegrees, 0f, 90f * dt);
            }

            position.y = Mathf.Min(position.y, altitudeMax);
            transform.position = position;
        }

        // ------------------------------------------------------------------ 歩行

        /// <summary>
        /// 飛行をやめる。**その場で人に戻り、飛んでいた勢いをそのまま持って落ちる。**
        ///
        /// 以前は真下の足場へ瞬間移動していた。移動が無いので慣性の持ちようがなく、
        /// 「飛行をやめたら進行方向へ大きく飛び出す」という当たり前の動きが出なかった。
        /// 足場を探さないので、空中でも海の上でもやめられる（落ちるだけ）。
        /// </summary>
        private void StopFlying()
        {
            if (body == null)
            {
                ShowNotice("この機体は歩けません（CharacterControllerが無い）");
                return;
            }

            Mode = MotionMode.Walking;

            // **速度を削らない。** 水平ぶんは慣性、垂直ぶんはそのまま落下（上昇中なら跳ね上がる）
            Vector3 velocity = transform.forward * EffectiveSpeed;
            walkVelocity = new Vector3(velocity.x, 0f, velocity.z);
            verticalVelocity = velocity.y;

            speed = walkVelocity.magnitude;
            rollDegrees = 0f;
            pitchDegrees = 0f;
            IsBoosting = false;

            transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            body.enabled = true;
            input?.ClearAim();
            ShowNotice("飛行をやめた");
        }

        /// <summary>
        /// その場から飛び立つ。
        ///
        /// **視線を動かさない。** 落下中に飛行へ切り替えた時、機首を既定の角度に立て直すと
        /// 見ていた景色が飛んで「操作を取り上げられた」感覚になる。押した瞬間に見ている方向を
        /// そのまま進行方向にする。
        /// </summary>
        private void Launch()
        {
            Mode = MotionMode.Flying;
            if (body != null) body.enabled = collideWhileFlying;

            pitchDegrees = Mathf.Clamp(pitchDegrees, -pitchLimit, pitchLimit);
            rollDegrees = 0f;

            // **歩行の勢いをそのまま飛行速度にする。** 走っていた速さも落下の速さも失わない
            float carried = new Vector3(walkVelocity.x, verticalVelocity, walkVelocity.z).magnitude;
            speed = Mathf.Max(launchSpeed, Mathf.Max(speed, carried));
            verticalVelocity = 0f;
            walkVelocity = Vector3.zero;

            // **飛び立った直後は上入力を捨てる。** 走り出す時はたいてい左スティックを
            // 前に倒しているので、そのまま飛行に移ると意図せず上昇し続けてしまう
            climbLockRemaining = climbLockSeconds;

            input?.ClearAim();
            ShowNotice("飛び立った");
        }

        private void UpdateWalking(FlightInputState state, float dt)
        {
            // 視線。マウス（または右スティック）で回し、身体は水平のまま
            float lookX = state.Aim.x + state.RightStick.x;
            float lookY = (state.Aim.y + state.RightStick.y) * (FlightSettings.InvertPitch ? 1f : -1f) + state.Arrows.y;
            LookInput = Vector2.zero; // 歩行中は視点がそのまま身体の向きなので、別に持たない

            yawDegrees += lookX * lookRate * dt;
            pitchDegrees = Mathf.Clamp(pitchDegrees + lookY * lookRate * dt, -80f, 80f);
            transform.rotation = Quaternion.Euler(-pitchDegrees, yawDegrees, 0f);

            // 移動は水平面のみ。視線が上を向いていても足元は水平に進む
            Vector2 move = Vector2.ClampMagnitude(state.Keys + state.LeftStick, 1f);

            // **上を向いてダッシュしたらそのまま飛び立つ。** 走って屋上の縁から跳ぶ動きと、
            // 飛行への移行が別操作だと、そこで動きが一度止まる。
            // **落下中もダッシュで飛べる。** 屋上から飛び降りてから飛ぶのが自然な動きで、
            // その時に上を向き直させるのは操作を増やすだけ
            bool falling = !body.isGrounded;
            if (state.Dash && (falling || (move.y > 0.3f && pitchDegrees >= seamlessLaunchPitch)))
            {
                speed = Mathf.Max(runSpeed, speed);

                // **スティックを倒している方向へ飛び出す。** 後ろ向きに走っている時に
                // 視線（＝背後）へ飛んでいくと、走っていた向きと逆に射出される
                if (move.sqrMagnitude > 0.01f)
                {
                    yawDegrees += Mathf.Atan2(move.x, move.y) * Mathf.Rad2Deg;
                }

                Launch();
                return;
            }

            Quaternion heading = Quaternion.Euler(0f, yawDegrees, 0f);
            float moveSpeed = state.Dash ? runSpeed : walkSpeed;
            Vector3 desired = heading * new Vector3(move.x, 0f, move.y) * moveSpeed;

            // **空中では慣性を持つ。** 地上のように入力＝速度にすると、飛び降りた勢いが
            // 足を離した瞬間に消えて、空中で止まって落ちるだけになる
            if (falling)
            {
                walkVelocity = Vector3.MoveTowards(
                    walkVelocity, desired * airControl, airAcceleration * dt);
            }
            else
            {
                // **着地直後は滑る。** 入力＝速度で上書きすると、飛んできた勢いが接地の瞬間に消える
                walkVelocity = Vector3.MoveTowards(walkVelocity, desired, groundAcceleration * dt);
            }

            Vector3 horizontal = walkVelocity;

            bool jumpHeld = state.Jump || state.TriggerLeft > 0.4f;
            bool jumpPressed = jumpHeld && !jumpHeldLastFrame;
            jumpHeldLastFrame = jumpHeld;

            if (body.isGrounded)
            {
                verticalVelocity = -2f; // 接地を維持する程度に押し付ける
                airJumpsUsed = 0;
                wallRunRemaining = wallRunSeconds;
                if (jumpPressed) verticalVelocity = jumpSpeed;
            }
            else if (CanWallRun(state, jumpHeld))
            {
                // 壁走り。壁に触れながらジャンプを押し続けている間だけ上がる
                verticalVelocity = wallRunSpeed;
                wallRunRemaining -= dt;
            }
            else
            {
                verticalVelocity -= gravity * dt;

                // 二段ジャンプ。屋上から屋上へ届かなかった時の救済でもある
                if (jumpPressed && airJumpsUsed < airJumpsMax)
                {
                    verticalVelocity = airJumpSpeed;
                    airJumpsUsed++;
                }
            }

            body.Move((horizontal + Vector3.up * verticalVelocity) * dt);
            speed = new Vector2(horizontal.x, horizontal.z).magnitude;

            // 海へ落ちても死なせない。沈む前に飛行へ戻す（→ CLAUDE.md 不変条件3）
            if (transform.position.y < seaLevel + altitudeAboveSeaMin) Launch();
        }

        /// <summary>
        /// 壁を駆け上がれるか。**壁に押し当てながらジャンプを押し続ける**のが条件。
        /// 屋上から屋上へ移る時に、届かない縁を登れると移動が途切れない。
        /// </summary>
        private bool CanWallRun(FlightInputState state, bool jumpHeld)
        {
            if (!jumpHeld || wallRunSeconds <= 0f || wallRunRemaining <= 0f) return false;
            if ((body.collisionFlags & CollisionFlags.Sides) == 0) return false;

            // 壁へ向かって進もうとしている時だけ。触れただけで登り始めると事故になる
            return (state.Keys + state.LeftStick).sqrMagnitude > 0.01f;
        }

        // ------------------------------------------------------------------ 共通

        /// <summary>
        /// 視点切替が押されたか。**押されていたらtrueを返して同時に消費する。**
        /// 入力は<see cref="FlightController"/>が1フレームに1回だけ読む（マウスのデルタを
        /// 二重に積まないため）ので、カメラ側はここ経由で受け取る。
        /// </summary>
        public bool ConsumeViewToggle()
        {
            if (!viewToggleRequested) return false;
            viewToggleRequested = false;
            return true;
        }

        private void ShowNotice(string message)
        {
            Notice = message;
            NoticeTime = Time.time;
        }

        /// <summary>開始地点・水平姿勢・初期速度に戻す。</summary>
        public void ResetPose()
        {
            Mode = MotionMode.Flying;
            if (body != null) body.enabled = collideWhileFlying;

            pitchDegrees = 0f;
            rollDegrees = 0f;
            yawDegrees = startYaw;
            speed = speedStart;
            verticalVelocity = 0f;
            walkVelocity = Vector3.zero;

            input?.ClearAim();
            transform.SetPositionAndRotation(startPosition, Quaternion.Euler(0f, startYaw, 0f));
        }
    }
}
