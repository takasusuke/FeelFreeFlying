using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PLATEAU.CityGML;
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
    /// そこで**属性の形を1つずつ変えながら**自前でシリアライズし、
    /// どの形で落ちるのかと、内部例外の中身を突き止める。
    ///
    /// これはM0のフレームレート計測とは別の問題だが、M3の見どころ自動抽出は
    /// 「属性がUnity側に残る」ことが前提なので、原因を残しておく必要がある。
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

            TryCase(report, "地物1つ・属性なし", () =>
            {
                var list = new CityObjectList();
                list.rootCityObjects.Add(NewCityObject("bldg_probe", new CityObjectList.Attributes()));
                return list;
            });

            TryCase(report, "属性: 文字列1つ", () =>
            {
                var attributes = new CityObjectList.Attributes();
                attributes.AddAttribute("用途", AttributeType.String, "住宅");
                return WrapInList(attributes);
            });

            TryCase(report, "属性: 整数と小数", () =>
            {
                var attributes = new CityObjectList.Attributes();
                attributes.AddAttribute("建築年", AttributeType.Integer, 1998);
                attributes.AddAttribute("高さ", AttributeType.Double, 31.5d);
                return WrapInList(attributes);
            });

            TryCase(report, "属性: 入れ子（AttributeSet）", () =>
            {
                var inner = new CityObjectList.Attributes();
                inner.AddAttribute("用途", AttributeType.String, "住宅");

                var outer = new CityObjectList.Attributes();
                outer.AddAttribute("建物情報", AttributeType.AttributeSet, inner);
                return WrapInList(outer);
            });

            TryCase(report, "属性: 入れ子の中に空のAttributes", () =>
            {
                var outer = new CityObjectList.Attributes();
                outer.AddAttribute("空", AttributeType.AttributeSet, new CityObjectList.Attributes());
                return WrapInList(outer);
            });

            TryCase(report, "属性: AttributeSetなのに中身がnull", () =>
            {
                var outer = new CityObjectList.Attributes();
                outer.AddAttribute("壊れた", AttributeType.AttributeSet, null);
                return WrapInList(outer);
            });

            string text = report.ToString();
            Debug.Log("[M0Probe]\n" + text);

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            File.WriteAllText(OutputPath, text, Encoding.UTF8);
            Debug.Log($"[M0Probe] 出力: {OutputPath}");
        }

        private static CityObjectList WrapInList(CityObjectList.Attributes attributes)
        {
            var list = new CityObjectList();
            list.rootCityObjects.Add(NewCityObject("bldg_probe", attributes));
            return list;
        }

        private static CityObjectList.CityObject NewCityObject(
            string gmlId, CityObjectList.Attributes attributes)
        {
            return new CityObjectList.CityObject().Init(
                gmlId, new[] { 0, 0 }, (ulong)CityObjectType.COT_Building, attributes);
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

                // **SDKは e.Message しかログしない。** 原因は内部例外の側にあるので全部辿る
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
