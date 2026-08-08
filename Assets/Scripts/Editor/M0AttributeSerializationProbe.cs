using System;
using System.IO;
using System.Text;
using PLATEAU.CityInfo;
using UnityEditor;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// インポート時に出た「メッシュデータの配置に失敗しました。」の原因を切り分ける（docs/m0-plan.md §3.1）。
    ///
    /// 実際のログでは例外が <c>Failed to serialize PLATEAU.CityInfo.CityObjectList value.</c> までしか
    /// 出ておらず、SDKが内部例外を握り潰している（<c>e.Message</c> だけをログしているため）。
    /// そこで最小のCityObjectListを自前でシリアライズし、**内部例外まで辿って原因を確定させる**。
    ///
    /// これはM0のフレームレート計測とは別の問題だが、M3の見どころ自動抽出は
    /// 「属性がUnity側に残る」ことが前提なので、M0のうちに理由を残しておく必要がある。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M0AttributeSerializationProbe.Probe
    /// </summary>
    public static class M0AttributeSerializationProbe
    {
        private const string OutputPath = "docs/m0/attribute-serialize-probe.txt";

        [MenuItem("Tools/FeelFreeFlying/M0: 属性シリアライズを試す")]
        public static void Probe()
        {
            var report = new StringBuilder();
            report.AppendLine($"unity      : {Application.unityVersion}");
            report.AppendLine($"messagepack: SDK同梱 3.1.4 (ThirdParty/MessagePack)");
            report.AppendLine();

            TryCase(report, "空のCityObjectList", () => new CityObjectList());

            string text = report.ToString();
            Debug.Log("[M0Probe]\n" + text);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, text, Encoding.UTF8);
            Debug.Log($"[M0Probe] 出力: {OutputPath}");
        }

        private static void TryCase(StringBuilder report, string caseName, Func<CityObjectList> build)
        {
            report.AppendLine($"--- {caseName} ---");
            try
            {
                CityObjectList list = build();
                byte[] bytes = CityObjectListSerializer.Serialize(list);
                report.AppendLine($"成功: {bytes.Length} bytes");
            }
            catch (Exception exception)
            {
                report.AppendLine("失敗:");
                for (Exception current = exception; current != null; current = current.InnerException)
                {
                    report.AppendLine($"  {current.GetType().FullName}: {current.Message}");
                }
                report.AppendLine("スタック:");
                report.AppendLine(exception.StackTrace);
            }
            report.AppendLine();
        }
    }
}
