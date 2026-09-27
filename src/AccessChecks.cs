using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Permissions;

// Unity's Mono skips visibility checks for assemblies that request SkipVerification,
// which lets us call internal/protected members of the (publicized) Assembly-CSharp.
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
[module: UnverifiableCode]
