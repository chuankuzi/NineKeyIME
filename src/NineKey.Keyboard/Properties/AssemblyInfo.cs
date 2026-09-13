// 本文件职责：WPF 主题字典定位与内部可见性程序集特性声明。
// 数据流位置：编译时嵌入元数据 → WPF 运行时定位 Generic.xaml 默认样式 → 测试项目访问 internal 成员。
// ⚠ 坑：SDK 在此开发机上未自动生成 ThemeInfo（旧版 WPF targets 缺陷），必须手动声明；
//       无主题字典，Generic.xaml 位于本源程序集，否则 KeyButton 默认样式无法加载。
// 相关规格：§13.18。

using System.Runtime.CompilerServices;
using System.Windows;

[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
[assembly: InternalsVisibleTo("NineKey.Keyboard.Tests")]
