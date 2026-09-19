using System.Reflection;

namespace NexusPipeline.ControlPlane.Http;

/// <summary>只负责反射读取 HTTP 路由元数据；不创建业务对象，也不读取 Host 容器。</summary>
internal static class ApiRouteCatalog
{
    internal sealed record RouteDefinition(
        Type HandlerType,
        MethodInfo Method,
        ApiBodyMode BodyMode,
        int MaxBodyBytes);

    internal sealed record BoundRoute(RouteDefinition Definition, object? Target);

    internal static IReadOnlyDictionary<string, RouteDefinition> Build(Assembly assembly)
    {
        var routes = new Dictionary<string, RouteDefinition>(StringComparer.OrdinalIgnoreCase);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        foreach (Type type in assembly.GetTypes())
        {
            ApiRouteAttribute? classAttribute = type.GetCustomAttribute<ApiRouteAttribute>();
            if (classAttribute is not null)
            {
                MethodInfo? handle = type.GetMethods(flags)
                    .FirstOrDefault(method => method.Name == "Handle");
                if (handle is not null)
                {
                    Add(routes, classAttribute.Name, new RouteDefinition(
                        type,
                        handle,
                        classAttribute.BodyMode,
                        classAttribute.MaxBodyBytes));
                }
            }

            foreach (MethodInfo method in type.GetMethods(flags))
            {
                ApiRouteAttribute? methodAttribute = method.GetCustomAttribute<ApiRouteAttribute>();
                if (methodAttribute is not null)
                {
                    Add(routes, methodAttribute.Name, new RouteDefinition(
                        type,
                        method,
                        methodAttribute.BodyMode,
                        methodAttribute.MaxBodyBytes));
                }
            }
        }
        return routes;
    }

    /// <summary>
    /// 将静态路由元数据绑定到 Host 组合根提供的 endpoint 对象。静态方法不需要 target；
    /// 非静态方法必须且只能找到一个同类型对象，避免把实例 handler 隐式退化成静态调用。
    /// </summary>
    internal static IReadOnlyDictionary<string, BoundRoute> Bind(
        Assembly assembly,
        IEnumerable<object> endpoints)
    {
        IReadOnlyDictionary<string, RouteDefinition> definitions = Build(assembly);
        Dictionary<Type, object[]> targets = endpoints
            .GroupBy(item => item.GetType())
            .ToDictionary(group => group.Key, group => group.Cast<object>().ToArray());
        var result = new Dictionary<string, BoundRoute>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, RouteDefinition definition) in definitions)
        {
            if (definition.Method.IsStatic)
            {
                result.Add(name, new BoundRoute(definition, null));
                continue;
            }
            object[] matches = targets.TryGetValue(definition.HandlerType, out object[]? exact)
                ? exact
                : Array.Empty<object>();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"实例 API 路由 {name} 的 endpoint 绑定数量应为 1，实际为 {matches.Length}：{definition.HandlerType.FullName}");
            }
            result.Add(name, new BoundRoute(definition, matches[0]));
        }
        return result;
    }

    private static void Add(
        IDictionary<string, RouteDefinition> routes,
        string name,
        RouteDefinition definition)
    {
        if (!routes.TryAdd(name, definition))
        {
            throw new InvalidOperationException($"重复的 API 路由名称：{name}");
        }
    }
}
