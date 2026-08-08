using UnityEditor;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M1の試遊ビルド（docs/m1-plan.md）。
    ///
    /// **操作感はEditorのGame Viewで判定しない。** フレームレートも入力の遅延も実行ファイルと違い、
    /// 「気持ちいいか」という判断が環境のせいで揺れる。M0の計測と同じく実行ファイルで試す。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M1PlayerBuild.BuildWindows64
    /// </summary>
    public static class M1PlayerBuild
    {
        private const string OutputDir = "Build/M1";
        private const string ExecutableName = "FeelFreeFlying-M1.exe";
        private const string FlightScene = "Assets/Scenes/M1Flight.unity";

        [MenuItem("Tools/FeelFreeFlying/M1: 試遊用にビルドする (Windows64)")]
        public static void BuildWindows64()
        {
            string[] scenes = { FlightScene };
            if (!PlayerBuildUtil.BuildWindows64(scenes, OutputDir, ExecutableName))
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
