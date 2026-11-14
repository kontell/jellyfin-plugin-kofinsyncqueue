using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.KofinSyncQueue.Tests;

/// <summary>
/// An interface implemented by handlers named after its members (a getter
/// is <c>get_Name</c>). Any other member returns a completed task or its
/// type's default, so a test names only what the code under test reads.
/// </summary>
public class Fake : DispatchProxy
{
    private IReadOnlyDictionary<string, Func<MethodInfo, object?[], object?>> _members =
        new Dictionary<string, Func<MethodInfo, object?[], object?>>();

    public static T Of<T>(IReadOnlyDictionary<string, Func<MethodInfo, object?[], object?>> members)
        where T : class
    {
        var fake = DispatchProxy.Create<T, Fake>();
        ((Fake)(object)fake)._members = members;
        return fake;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (_members.TryGetValue(method.Name, out var member))
        {
            return member(method, args ?? Array.Empty<object?>());
        }

        if (method.ReturnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType)
            : null;
    }
}
