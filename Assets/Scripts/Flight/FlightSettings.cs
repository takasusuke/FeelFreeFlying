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

    /// <summary>
    /// 操作設定。**人によって好みが割れるところだけを画面から変えられるようにする**
    /// （`SettingsScreen`）。値は<see cref="PlayerPrefs"/>に残すので、次に起動しても同じ。
    ///
    /// ボタンの割り当ては<see cref="FlightBindings"/>が持つ。
    /// </summary>
    public static class FlightSettings
    {
        private const string SteeringKey = "ff.steering";

        /// <summary>M1までの設定キー。**上下反転が1つしか無かった頃のもの**（→ 下記）。</summary>
        private const string LegacyInvertKey = "ff.invertPitch";

        private const string InvertFlightKey = "ff.invertFlightPitch";
        private const string InvertLookKey = "ff.invertLookPitch";

        private static SteeringMode steering = (SteeringMode)PlayerPrefs.GetInt(SteeringKey, 0);

        // **反転は2つに分けた。** 機首の上下と視点の上下は、同じ人でも好みが逆になる
        // （飛行機は倒すと下を向くが、カメラは上を向くのが自然、という感覚）。
        // 古い設定は両方の初期値として引き継ぐ
        private static bool invertFlightPitch =
            PlayerPrefs.GetInt(InvertFlightKey, PlayerPrefs.GetInt(LegacyInvertKey, 1)) != 0;

        private static bool invertLookPitch =
            PlayerPrefs.GetInt(InvertLookKey, PlayerPrefs.GetInt(LegacyInvertKey, 1)) != 0;

        public static SteeringMode Steering
        {
            get => steering;
            set { steering = value; PlayerPrefs.SetInt(SteeringKey, (int)value); PlayerPrefs.Save(); }
        }

        /// <summary>飛行中の機首の上下を反転する。</summary>
        public static bool InvertFlightPitch
        {
            get => invertFlightPitch;
            set { invertFlightPitch = value; PlayerPrefs.SetInt(InvertFlightKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>視点（カメラ）の上下を反転する。歩行中の見回しと三人称のカメラ。</summary>
        public static bool InvertLookPitch
        {
            get => invertLookPitch;
            set { invertLookPitch = value; PlayerPrefs.SetInt(InvertLookKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static string SteeringLabel(SteeringMode mode) => mode switch
        {
            SteeringMode.IndependentView => "視点と進路を分ける（左スティックで進む）",
            SteeringMode.FollowView => "視線の方向へ飛ぶ（右スティックで進む）",
            _ => mode.ToString(),
        };
    }
}
