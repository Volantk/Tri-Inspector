using System;
using System.Reflection;
using TriInspector.Utilities;
using UnityEngine;

namespace TriInspector.Resolvers
{
    internal sealed class ContextMemberValueResolver<T> : ValueResolver<T>
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly string _expression;
        private readonly ContextKind _contextKind;
        private readonly string _memberName;
        private readonly Type _ancestorType;
        private readonly string _error;

        private bool _loggedRuntimeError;

        public static bool TryResolve(TriPropertyDefinition propertyDefinition, string expression,
            out ValueResolver<T> resolver)
        {
            resolver = null;

            if (expression == null || !expression.StartsWith("@"))
            {
                return false;
            }

            resolver = Parse(expression);
            return true;
        }

        private static ValueResolver<T> Parse(string expression)
        {
            var body = expression.Substring(1);
            Type ancestorType = null;
            ContextKind contextKind;
            string memberName;

            if (body.StartsWith("ancestor(", StringComparison.Ordinal))
            {
                var endIndex = body.IndexOf(").", StringComparison.Ordinal);
                if (endIndex < 0)
                {
                    return new ContextMemberValueResolver<T>(expression,
                        "Context expression must be formatted as '@ancestor(Type.FullName).Member'");
                }

                var typeName = body.Substring("ancestor(".Length, endIndex - "ancestor(".Length);
                memberName = body.Substring(endIndex + 2);
                contextKind = ContextKind.Ancestor;

                if (!TriReflectionUtilities.TryFindTypeByFullName(typeName, out ancestorType))
                {
                    return new ContextMemberValueResolver<T>(expression, $"Cannot find type '{typeName}'");
                }
            }
            else
            {
                var separatorIndex = body.IndexOf('.');
                if (separatorIndex < 0)
                {
                    return new ContextMemberValueResolver<T>(expression,
                        "Context expression must be formatted as '@root.Member', '@parent.Member', '@owner.Member', or '@ancestor.Member'");
                }

                var contextName = body.Substring(0, separatorIndex);
                memberName = body.Substring(separatorIndex + 1);

                switch (contextName)
                {
                    case "owner":
                        contextKind = ContextKind.Owner;
                        break;
                    case "parent":
                        contextKind = ContextKind.Parent;
                        break;
                    case "root":
                        contextKind = ContextKind.Root;
                        break;
                    case "ancestor":
                        contextKind = ContextKind.Ancestor;
                        break;
                    default:
                        return new ContextMemberValueResolver<T>(expression,
                            $"Unknown context '{contextName}'. Supported contexts: owner, parent, root, ancestor");
                }
            }

            if (string.IsNullOrEmpty(memberName))
            {
                return new ContextMemberValueResolver<T>(expression, "Context expression member name is empty");
            }

            return new ContextMemberValueResolver<T>(expression, contextKind, memberName, ancestorType);
        }

        private ContextMemberValueResolver(string expression, string error)
        {
            _expression = expression;
            _error = error;
        }

        private ContextMemberValueResolver(string expression, ContextKind contextKind, string memberName, Type ancestorType)
        {
            _expression = expression;
            _contextKind = contextKind;
            _memberName = memberName;
            _ancestorType = ancestorType;
        }

        public override bool TryGetErrorString(out string error)
        {
            error = _error;
            return _error != null;
        }

        public override T GetValue(TriProperty property, T defaultValue = default)
        {
            if (_error != null)
            {
                return defaultValue;
            }

            try
            {
                switch (_contextKind)
                {
                    case ContextKind.Owner:
                        return TryGetMemberValue(property.Owner, defaultValue, out var ownerValue)
                            ? ownerValue
                            : LogRuntimeError(defaultValue);

                    case ContextKind.Parent:
                        return TryFindInParent(property, defaultValue, out var parentValue)
                            ? parentValue
                            : LogRuntimeError(defaultValue);

                    case ContextKind.Root:
                        return TryGetMemberValue(property.PropertyTree.RootProperty, defaultValue, out var rootValue)
                            ? rootValue
                            : LogRuntimeError(defaultValue);

                    case ContextKind.Ancestor:
                        return TryFindInAncestors(property, defaultValue, out var ancestorValue)
                            ? ancestorValue
                            : LogRuntimeError(defaultValue);

                    default:
                        return defaultValue;
                }
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException targetInvocationException)
                {
                    e = targetInvocationException.InnerException;
                }

                Debug.LogException(e);
                return defaultValue;
            }
        }

        private bool TryFindInParent(TriProperty property, T defaultValue, out T value)
        {
            var parent = property.Owner?.Parent;
            while (parent != null && (parent.IsArray || parent.IsArrayElement))
            {
                parent = parent.Parent;
            }

            if (parent != null && TryGetMemberValue(parent, defaultValue, out value))
            {
                return true;
            }

            value = defaultValue;
            return false;
        }

        private bool TryFindInAncestors(TriProperty property, T defaultValue, out T value)
        {
            var parent = property.Owner?.Parent;
            while (parent != null)
            {
                if (!parent.IsArray && !parent.IsArrayElement)
                {
                    var parentValue = parent.GetValue(0);
                    if (parentValue != null &&
                        (_ancestorType == null || _ancestorType.IsInstanceOfType(parentValue)) &&
                        TryGetMemberValue(parentValue, defaultValue, out value))
                    {
                        return true;
                    }
                }

                parent = parent.Parent;
            }

            value = defaultValue;
            return false;
        }

        private bool TryGetMemberValue(TriProperty property, T defaultValue, out T value)
        {
            if (property == null)
            {
                value = defaultValue;
                return false;
            }

            return TryGetMemberValue(property.GetValue(0), defaultValue, out value);
        }

        private bool TryGetMemberValue(object target, T defaultValue, out T value)
        {
            value = defaultValue;

            if (target == null)
            {
                return false;
            }

            var targetType = target.GetType();

            foreach (var fieldInfo in targetType.GetFields(InstanceFlags))
            {
                if (fieldInfo.Name == _memberName && IsAssignableMemberType(fieldInfo.FieldType))
                {
                    value = (T) fieldInfo.GetValue(target);
                    return true;
                }
            }

            foreach (var propertyInfo in targetType.GetProperties(InstanceFlags))
            {
                if (propertyInfo.Name == _memberName &&
                    propertyInfo.CanRead &&
                    IsAssignableMemberType(propertyInfo.PropertyType))
                {
                    value = (T) propertyInfo.GetValue(target);
                    return true;
                }
            }

            foreach (var methodInfo in targetType.GetMethods(InstanceFlags))
            {
                if (methodInfo.Name == _memberName &&
                    IsAssignableMemberType(methodInfo.ReturnType) &&
                    methodInfo.GetParameters() is var parameterInfos &&
                    parameterInfos.Length == 0)
                {
                    value = (T) methodInfo.Invoke(target, Array.Empty<object>());
                    return true;
                }
            }

            return false;
        }

        private static bool IsAssignableMemberType(Type memberType)
        {
            return memberType != typeof(void) && typeof(T).IsAssignableFrom(memberType);
        }

        private T LogRuntimeError(T defaultValue)
        {
            if (!_loggedRuntimeError)
            {
                _loggedRuntimeError = true;
                Debug.LogError($"Context expression '{_expression}' could not find member '{_memberName}' with return type '{typeof(T).Name}'. " +
                               "Check that the member exists on the selected context, returns the expected type, and has no parameters if it is a method.");
            }

            return defaultValue;
        }

        private enum ContextKind
        {
            Owner,
            Parent,
            Root,
            Ancestor,
        }
    }
}
