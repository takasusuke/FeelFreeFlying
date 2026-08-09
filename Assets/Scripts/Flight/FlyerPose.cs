using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 飛行と歩行で見た目の姿勢を入れ替える。
    ///
    /// 飛ぶ姿勢（腕を前に伸ばして水平）のまま屋上を歩くと、**空を飛ぶ格好で滑っていく**ように見える。
    /// 同じモデルを回転させるだけでは、腕が真上を向いた立ち姿になって直らないので、
    /// 姿勢ごとに別のオブジェクトを用意して切り替える。
    ///
    /// 歩行中は身体を起こしたままにする。機体の回転は視線に合わせて上下するが、
    /// **人が空を見上げても身体は倒れない**ので、見上げたぶんを打ち消す。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlyerPose : MonoBehaviour
    {
        [SerializeField] private FlightController controller;
        [SerializeField] private GameObject flyingPose;
        [SerializeField] private GameObject walkingPose;

        private void Awake()
        {
            if (controller == null) controller = GetComponent<FlightController>();
            Apply();
        }

        private void LateUpdate() => Apply();

        private void Apply()
        {
            if (controller == null) return;

            bool walking = controller.IsWalking;

            if (flyingPose != null && flyingPose.activeSelf == walking) flyingPose.SetActive(!walking);
            if (walkingPose != null && walkingPose.activeSelf != walking) walkingPose.SetActive(walking);

            if (!walking || walkingPose == null) return;

            // 親（機体）は視線ぶん傾いているので、その回転を打ち消して立たせる
            walkingPose.transform.localRotation = Quaternion.Euler(controller.PitchDegrees, 0f, 0f);
        }
    }
}
