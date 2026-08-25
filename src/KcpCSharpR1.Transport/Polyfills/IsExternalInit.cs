#if !NET5_0_OR_GREATER
using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// netstandard2.1 타깃에서 <c>init</c> 접근자를 쓰기 위한 컴파일러 셔틀.
/// </summary>
/// <remarks>
/// 이 타입은 C# 컴파일러가 <c>init</c> 세터를 만들 때 참조만 하고 런타임에는 쓰이지 않는다.
/// .NET 5 이상에는 프레임워크에 이미 있으므로 그때는 컴파일에서 제외한다.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
#endif
