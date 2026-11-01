using System;
using FlaxEngine;
using EKEditorUIKit;

namespace FlaxAssetManager.UI
{
    /// <summary>
    /// A list row that forwards ArrowUp/ArrowDown to the window so it can move focus
    /// (and thus selection) exactly like Tab/Shift+Tab. Without this, the focused row
    /// never surfaces those keys to the window and arrows do nothing.
    /// </summary>
    public sealed class AssetManagerListRow : ListRow
    {
        // true = ArrowDown, false = ArrowUp. Invoked instead of the key bubbling on.
        public Action<bool> OnArrow;

        public override bool OnKeyDown(KeyboardKeys key)
        {
            if (key == KeyboardKeys.ArrowDown)
            {
                OnArrow?.Invoke(true);
                return true;
            }
            if (key == KeyboardKeys.ArrowUp)
            {
                OnArrow?.Invoke(false);
                return true;
            }
            return base.OnKeyDown(key);
        }
    }
}
