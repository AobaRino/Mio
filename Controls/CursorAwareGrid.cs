using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Mio.Controls;

/// <summary>
/// UIElement.ProtectedCursor 是 protected 成员，只能由派生类访问。
/// 这个 Grid 的唯一职责就是把光标控制暴露出来，供播放时隐藏鼠标指针。
/// </summary>
public class CursorAwareGrid : Grid
{
    private static readonly InputCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    private bool _isCursorVisible = true;

    public void SetCursorVisible(bool visible)
    {
        if (_isCursorVisible == visible)
        {
            return;
        }

        _isCursorVisible = visible;
        ProtectedCursor = visible ? ArrowCursor : null;
    }
}
