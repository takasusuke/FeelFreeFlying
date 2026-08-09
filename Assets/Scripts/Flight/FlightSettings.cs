using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>操縦方式。**どちらが良いかはM1で決める**（→ docs/m1-plan.md §2）。</summary>
    public enum SteeringMode
    {
        /// <summary>左スティックで進み、右スティックは視点だけ。高度を保って下を見られる。</summary>
        IndependentView = 0,

        /// <summary>右スティック（視線）の方向へ飛ぶ。スパイダーマン系の飛行に近い。</summary>
        FollowView = 1,
    }

    /// <summary>ジャンプの割り当て。右スティックを操作しながらでも押せるかが分かれ目。</summary>
    public enum JumpBinding
    {
        LeftTrigger = 0,
        Cross = 1,
    }

    /// <summary>
    /// 操作設定。**試遊で比べるための切り替えなので、コードではなく画面から変えられる**
    /// （`SettingsScreen`）。値は<see cref="PlayerPrefs"/>に残すので、次に起動しても同じ。
    ///
    /// 操作方式が固まったら（`requirements.md` §11）、選択肢を減らして既定に畳む。
    /// </summary>
    public static class FlightSettings
    {
        private const string SteeringKey = "ff.steering";
        private const string InvertKey = "ff.invertPitch";
        private const string JumpKey = "ff.jump";

        private static SteeringMode steering = (SteeringMode)PlayerPrefs.GetInt(SteeringKey, 0);
        private static bool invertPitch = PlayerPrefs.GetInt(InvertKey, 1) != 0;
        private static JumpBinding jump = (JumpBinding)PlayerPrefs.GetInt(JumpKey, 0);

        public static SteeringMode Steering
        {
            get => steering;
            set { steering = value; PlayerPrefs.SetInt(SteeringKey, (int)value); PlayerPrefs.Save(); }
        }

        public static bool InvertPitch
        {
            get => invertPitch;
            set { invertPitch = value; PlayerPrefs.SetInt(InvertKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static JumpBinding Jump
        {
            get => jump;
            set { jump = value; PlayerPrefs.SetInt(JumpKey, (int)value); PlayerPrefs.Save(); }
        }

        public static string SteeringLabel(SteeringMode mode) => mode switch
        {
            SteeringMode.IndependentView => "視点と進路を分ける（左スティックで進む）",
            SteeringMode.FollowView => "視線の方向へ飛ぶ（右スティックで進む）",
            _ => mode.ToString(),
        };

        public static string JumpLabel(JumpBinding binding) => binding switch
        {
            JumpBinding.LeftTrigger => "L2",
            JumpBinding.Cross => "×",
            _ => binding.ToString(),
        };
    }
}
