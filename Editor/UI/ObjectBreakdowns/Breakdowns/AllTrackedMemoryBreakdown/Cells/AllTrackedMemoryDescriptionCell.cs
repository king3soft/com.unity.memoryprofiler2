#if UNITY_2022_1_OR_NEWER
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.UI
{
    class AllTrackedMemoryDescriptionCell : VisualElement
    {
        const string k_UxmlAssetGuid = "e6675b31a9679be438e9a4ea92f56511";
        const string k_UxmlIdentifier_Label = "all-tracked-memory-description-cell__label";
        const string k_UxmlIdentifier_SecondaryLabel = "all-tracked-memory-description-cell__secondary-label";

        Label m_Label;
        Label m_SecondaryLabel;

        void Initialize()
        {
            m_Label = this.Q<Label>(k_UxmlIdentifier_Label);
            m_SecondaryLabel = this.Q<Label>(k_UxmlIdentifier_SecondaryLabel);
        }

        public static AllTrackedMemoryDescriptionCell Instantiate()
        {
            var cell = (AllTrackedMemoryDescriptionCell)ViewControllerUtility.LoadVisualTreeFromUxml(k_UxmlAssetGuid);
            cell.Initialize();
            return cell;
        }

        public void SetText(string text)
        {
            m_Label.text = text;
        }

        public void SetSecondaryText(string text)
        {
            m_SecondaryLabel.text = text;
        }

        public new class UxmlFactory : UxmlFactory<AllTrackedMemoryDescriptionCell> { }
    }
}
#endif
