using System.IO;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 光の柱（<see cref="SpotBeacons"/>）を試遊シーンに載せる（docs/m3-plan.md §4.1）。
    ///
    /// **マテリアルは資産として作る。** 実行時に<see cref="Shader.Find"/>で組み立てると、
    /// そのシェーダを誰も参照していないためビルドから落ち、
    /// **エディタでは見えるのにビルドではピンク**になる。
    /// </summary>
    public static class M3SpotSetup
    {
        private const string MaterialPath = "Assets/Settings/Rendering/SpotBeacon.mat";

        /// <summary>
        /// 見どころの柱をシーンに足す。**スポットが1つも無くても足す**——
        /// 抽出をやり直せば次のビルドから効くようにしておく。
        /// </summary>
        public static void AddBeacons(Transform viewer)
        {
            var beaconObject = new GameObject("SpotBeacons");
            SpotBeacons beacons = beaconObject.AddComponent<SpotBeacons>();

            var serialized = new SerializedObject(beacons);
            serialized.FindProperty("viewer").objectReferenceValue = viewer;
            serialized.FindProperty("beaconMaterial").objectReferenceValue = LoadOrCreateMaterial();
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// 加算合成の無地マテリアル。色と濃さは<see cref="SpotBeacons"/>が
        /// <see cref="MaterialPropertyBlock"/>で差し替えるので、ここでは混ぜ方だけ決める。
        /// </summary>
        private static Material LoadOrCreateMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogWarning("[M3Setup] URPのUnlitシェーダが見つかりません。柱は出ません。");
                return null;
            }

            var material = new Material(shader) { name = "SpotBeacon" };

            // URPのUnlitを透明・加算にする決まり文句。**キーワードまで揃えないと効かない**
            material.SetFloat("_Surface", 1f);   // Transparent
            material.SetFloat("_Blend", 2f);     // Additive
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.One);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetColor("_BaseColor", Color.white);

            // **影を落とさない。** 柱は光であって物ではない
            material.SetShaderPassEnabled("ShadowCaster", false);

            Directory.CreateDirectory(Path.GetDirectoryName(MaterialPath));
            AssetDatabase.CreateAsset(material, MaterialPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[M3Setup] 柱のマテリアルを作成: {MaterialPath}");
            return material;
        }
    }
}
