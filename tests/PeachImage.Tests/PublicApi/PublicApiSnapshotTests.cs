using System.Reflection;
using System.Text;

namespace PeachImage.Tests.PublicApi;

/// <summary>
/// Pins PeachImage's exported surface to <c>PublicApi.approved.txt</c>, so an addition, rename or removal is a visible diff in review.
/// After an intended change, set <c>PEACH_UPDATE_PUBLIC_API=1</c> and run this test to rewrite the file in the source tree.
/// </summary>
public class PublicApiSnapshotTests
{
    private const string ApprovedFile = "PublicApi.approved.txt";

    [Fact]
    public void ExportedSurface_MatchesApprovedSnapshot()
    {
        string actual = Dump(typeof(Image).Assembly);

        if (Environment.GetEnvironmentVariable("PEACH_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(SourcePath(), actual);
            return;
        }

        string approved = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PublicApi", ApprovedFile)).Replace("\r\n", "\n");
        Assert.True(
            approved == actual.Replace("\r\n", "\n"),
            "The public API changed. If that is intended, rerun this test with PEACH_UPDATE_PUBLIC_API=1 and commit PublicApi/PublicApi.approved.txt.");
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string? thisFile = null) =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, ApprovedFile);

    private static string Dump(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class" : type.IsSealed ? "sealed class" : type.IsAbstract ? "abstract class" : "class";
            lines.Add($"{kind} {type.FullName}{(type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum) ? " : " + b.FullName : string.Empty)}");

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var members = new List<string>();
            foreach (var member in type.GetMembers(flags))
            {
                switch (member)
                {
                    case FieldInfo f when f.IsPublic || f.IsFamily:
                        members.Add($"  field {Name(f.FieldType)} {f.Name}{(f.IsStatic ? " static" : string.Empty)}{(type.IsEnum && f.IsLiteral ? " = " + f.GetRawConstantValue() : string.Empty)}");
                        break;
                    case MethodBase m when (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) && !(m is MethodInfo mi && mi.IsSpecialName):
                        members.Add($"  {(m.IsConstructor ? "ctor" : "method " + Name(((MethodInfo)m).ReturnType))} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : string.Empty)))}){(m.IsStatic ? " static" : string.Empty)}");
                        break;
                    case PropertyInfo p when (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false) || (p.GetMethod?.IsFamily ?? false):
                        members.Add($"  property {Name(p.PropertyType)} {p.Name}{(p.GetMethod?.IsPublic ?? false ? " get" : string.Empty)}{(p.SetMethod is { IsPublic: true } ? (p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Length > 0 ? " init" : " set") : string.Empty)}{((p.GetMethod ?? p.SetMethod)!.IsStatic ? " static" : string.Empty)}");
                        break;
                }
            }

            lines.AddRange(members.Order(StringComparer.Ordinal));
        }

        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string Name(Type type) => System.Text.RegularExpressions.Regex.Replace(RawName(type), @"\[\[([^\],]+),[^\]]*\]\]", "$1");

    private static string RawName(Type type) =>
        type.IsGenericType
            ? $"{type.GetGenericTypeDefinition().FullName?.Split('`')[0]}<{string.Join(",", type.GetGenericArguments().Select(Name))}>"
            : type.IsArray ? Name(type.GetElementType()!) + "[]" : type.FullName ?? type.Name;
}
