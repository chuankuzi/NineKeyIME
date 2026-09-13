// 本文件职责：库内平台无关的二维点类型，替代 System.Windows.Point（纯逻辑库不得依赖 WPF 程序集）。
// 数据流位置：壳侧（KeyButton 等）把 WPF Point 机械转换为 TouchPoint2D 后送入纯逻辑组件（TouchGhostFilter）。

namespace NineKey.Keyboard.Input;

/// <summary>平台无关的二维点（双精度）。仅承载坐标数据，不含任何 UI 语义。</summary>
/// <param name="X">横坐标（像素）。</param>
/// <param name="Y">纵坐标（像素）。</param>
public readonly record struct TouchPoint2D(double X, double Y);
