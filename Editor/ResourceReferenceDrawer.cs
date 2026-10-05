using UnityEditor;
using UnityEngine;

namespace ZRAsset.Editor
{
    [CustomPropertyDrawer(typeof(ResourceReference), true)]
    public sealed class ResourceReferenceDrawer: PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            SerializedProperty guid = property.FindPropertyRelative("m_guid");
            Object asset = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid.stringValue));
            EditorGUI.BeginChangeCheck();
            Object next = EditorGUI.ObjectField(position, label, asset, typeof(Object), false);
            if (EditorGUI.EndChangeCheck()) {
                if (next == null) {
                    guid.stringValue = "";
                }
                else if (!AssetDatabase.IsSubAsset(next) && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(next))) {
                    guid.stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(next));
                }
            }
            EditorGUI.EndProperty();
        }
    }
}
