#if UNITY_2022_1_OR_NEWER
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.UI
{
    class UnityObjectsDescriptionCell : VisualElement
    {
        const string k_UxmlAssetGuid = "1fec5c07542077c4d81d5cb90b89c7b3";
        const string k_UxmlIdentifier_Icon = "unity-objects-description-cell__icon";
        const string k_UxmlIdentifier_Label = "unity-objects-description-cell__label";
        const string k_UxmlIdentifier_SecondaryLabel = "unity-objects-description-cell__secondary-label";
        const string k_NoTypeIconFilePath = "Packages/com.unity.memoryprofiler/Package Resources/Icons/NoIconIcon.png";

        VisualElement m_Icon;
        Label m_Label;
        Label m_SecondaryLabel;

        void Initialize()
        {
            m_Icon = this.Q<VisualElement>(k_UxmlIdentifier_Icon);
            m_Label = this.Q<Label>(k_UxmlIdentifier_Label);
            m_SecondaryLabel = this.Q<Label>(k_UxmlIdentifier_SecondaryLabel);
        }

        public static UnityObjectsDescriptionCell Instantiate()
        {
            var cell = (UnityObjectsDescriptionCell)ViewControllerUtility.LoadVisualTreeFromUxml(k_UxmlAssetGuid);
            cell.Initialize();
            return cell;
        }

        public void SetIconForTypeName(string typeName)
        {
            var iconName = $"{typeName} Icon";
            var icon = IconUtility.LoadBuiltInIconWithName(iconName);
            if (icon == null)
                icon = IconUtility.LoadIconAtPath(k_NoTypeIconFilePath);
            m_Icon.style.backgroundImage = icon;
        }

        public void SetText(string text)
        {
            m_Label.text = text;
        }

        public void SetSecondaryText(string text)
        {
            m_SecondaryLabel.text = text;
        }

        public new class UxmlFactory : UxmlFactory<UnityObjectsDescriptionCell> { }
    }
}
#endif
