using System;
using System.Collections.Generic;
using System.Reflection;
using TriInspector.Utilities;
using UnityEngine;

namespace TriInspector.Resolvers
{
    internal sealed class ContextMemberValueResolver<T> : ValueResolver<T>
    {
        private readonly ContextExpression _expression;
        private bool _loggedRuntimeError;

        public static bool TryResolve(TriPropertyDefinition propertyDefinition, string expression,
            out ValueResolver<T> resolver)
        {
            resolver = null;

            if (!ContextExpressionUtility.IsContextExpression(expression))
            {
                return false;
            }

            resolver = new ContextMemberValueResolver<T>(ContextExpressionUtility.Parse(expression));
            return true;
        }

        private ContextMemberValueResolver(ContextExpression expression)
        {
            _expression = expression;
        }

        public override bool TryGetErrorString(out string error)
        {
            error = _expression.Error;
            return _expression.Error != null;
        }

        public override T GetValue(TriProperty property, T defaultValue = default)
        {
            if (_expression.Error != null)
            {
                return defaultValue;
            }

            try
            {
                return ContextExpressionUtility.TryGetValue<T>(_expression, property, 0, out var value, out var error)
                    ? value
                    : LogRuntimeError(defaultValue, error);
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

        private T LogRuntimeError(T defaultValue, string error)
        {
            if (!_loggedRuntimeError)
            {
                _loggedRuntimeError = true;
                Debug.LogError(error ??
                               $"Context expression '{_expression.Source}' could not find member '{_expression.MemberName}' with return type '{typeof(T).Name}'. " +
                               "Check that the member exists on the selected context, returns the expected type, and has compatible parameters.");
            }

            return defaultValue;
        }
    }

    internal sealed class ContextActionResolver : ActionResolver
    {
        private readonly ContextExpression _expression;
        private bool _loggedRuntimeError;

        public static bool TryResolve(TriPropertyDefinition propertyDefinition, string expression,
            out ActionResolver resolver)
        {
            resolver = null;

            if (!ContextExpressionUtility.IsContextExpression(expression))
            {
                return false;
            }

            resolver = new ContextActionResolver(ContextExpressionUtility.Parse(expression));
            return true;
        }

        private ContextActionResolver(ContextExpression expression)
        {
            _expression = expression;
        }

        public override bool TryGetErrorString(out string error)
        {
            error = _expression.Error;
            return _expression.Error != null;
        }

        public override void InvokeForTarget(TriProperty property, int targetIndex)
        {
            if (_expression.Error != null)
            {
                return;
            }

            try
            {
                if (!ContextExpressionUtility.TryInvokeAction(_expression, property, targetIndex, out var error))
                {
                    LogRuntimeError(error);
                }
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException targetInvocationException)
                {
                    e = targetInvocationException.InnerException;
                }

                Debug.LogException(e);
            }
        }

        private void LogRuntimeError(string error)
        {
            if (_loggedRuntimeError)
            {
                return;
            }

            _loggedRuntimeError = true;
            Debug.LogError(error ??
                           $"Context action expression '{_expression.Source}' could not find method '{_expression.MemberName}'. " +
                           "Check that the method exists on the selected context, returns void, and has compatible parameters.");
        }
    }

    internal sealed class ContextExpression
    {
        public string Source;
        public ContextKind ContextKind;
        public Type AncestorType;
        public string MemberName;
        public bool IsMethodCall;
        public string[] Arguments;
        public string Error;
    }

    internal enum ContextKind
    {
        Owner,
        Parent,
        Root,
        Ancestor,
    }

    internal static class ContextExpressionUtility
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static bool IsContextExpression(string expression)
        {
            return expression != null && expression.StartsWith("@");
        }

        public static ContextExpression Parse(string expression)
        {
            var result = new ContextExpression
            {
                Source = expression,
                Arguments = Array.Empty<string>(),
            };

            var body = expression.Substring(1);
            string memberExpression;

            if (body.StartsWith("ancestor(", StringComparison.Ordinal))
            {
                var endIndex = body.IndexOf(").", StringComparison.Ordinal);
                if (endIndex < 0)
                {
                    result.Error = "Context expression must be formatted as '@ancestor(Type.FullName).Member'";
                    return result;
                }

                var typeName = body.Substring("ancestor(".Length, endIndex - "ancestor(".Length);
                memberExpression = body.Substring(endIndex + 2);
                result.ContextKind = ContextKind.Ancestor;

                if (!TriReflectionUtilities.TryFindTypeByFullName(typeName, out result.AncestorType))
                {
                    result.Error = $"Cannot find type '{typeName}'";
                    return result;
                }
            }
            else
            {
                var separatorIndex = body.IndexOf('.');
                if (separatorIndex < 0)
                {
                    result.Error = "Context expression must be formatted as '@root.Member', '@parent.Member', '@owner.Member', or '@ancestor.Member'";
                    return result;
                }

                var contextName = body.Substring(0, separatorIndex);
                memberExpression = body.Substring(separatorIndex + 1);

                switch (contextName)
                {
                    case "owner":
                        result.ContextKind = ContextKind.Owner;
                        break;
                    case "parent":
                        result.ContextKind = ContextKind.Parent;
                        break;
                    case "root":
                        result.ContextKind = ContextKind.Root;
                        break;
                    case "ancestor":
                        result.ContextKind = ContextKind.Ancestor;
                        break;
                    default:
                        result.Error = $"Unknown context '{contextName}'. Supported contexts: owner, parent, root, ancestor";
                        return result;
                }
            }

            ParseMemberExpression(memberExpression, result);
            return result;
        }

        public static bool TryGetValue<T>(ContextExpression expression, TriProperty property, int targetIndex,
            out T value, out string error)
        {
            value = default;
            error = null;

            return expression.ContextKind == ContextKind.Ancestor && expression.AncestorType == null
                ? TryFindAncestorValue(expression, property, targetIndex, out value, out error)
                : TryGetContextObject(expression, property, targetIndex, out var target, out error) &&
                  TryGetValueOnTarget(expression, target, property, targetIndex, out value, out error);
        }

        public static bool TryInvokeAction(ContextExpression expression, TriProperty property, int targetIndex,
            out string error)
        {
            error = null;

            return expression.ContextKind == ContextKind.Ancestor && expression.AncestorType == null
                ? TryFindAncestorAction(expression, property, targetIndex, out error)
                : TryGetContextObject(expression, property, targetIndex, out var target, out error) &&
                  TryInvokeActionOnTarget(expression, target, property, targetIndex, out error);
        }

        private static void ParseMemberExpression(string memberExpression, ContextExpression result)
        {
            memberExpression = memberExpression.Trim();

            var openParenIndex = memberExpression.IndexOf('(');
            if (openParenIndex >= 0)
            {
                if (!memberExpression.EndsWith(")", StringComparison.Ordinal))
                {
                    result.Error = $"Context expression '{result.Source}' has an invalid method call";
                    return;
                }

                result.IsMethodCall = true;
                result.MemberName = memberExpression.Substring(0, openParenIndex).Trim();

                var args = memberExpression.Substring(openParenIndex + 1,
                    memberExpression.Length - openParenIndex - 2).Trim();
                result.Arguments = string.IsNullOrEmpty(args) ? Array.Empty<string>() : SplitArguments(args);
            }
            else
            {
                result.MemberName = memberExpression;
            }

            if (string.IsNullOrEmpty(result.MemberName))
            {
                result.Error = "Context expression member name is empty";
            }
        }

        private static string[] SplitArguments(string args)
        {
            var split = args.Split(',');
            for (var i = 0; i < split.Length; i++)
            {
                split[i] = split[i].Trim();
            }

            return split;
        }

        private static bool TryFindAncestorValue<T>(ContextExpression expression, TriProperty property, int targetIndex,
            out T value, out string error)
        {
            value = default;
            error = null;

            var parent = property.Owner?.Parent;
            while (parent != null)
            {
                if (!parent.IsArray && !parent.IsArrayElement &&
                    TryGetValueOnTarget(expression, parent.GetValue(targetIndex), property, targetIndex, out value, out _))
                {
                    return true;
                }

                parent = parent.Parent;
            }

            error = CreateValueError<T>(expression);
            return false;
        }

        private static bool TryFindAncestorAction(ContextExpression expression, TriProperty property, int targetIndex,
            out string error)
        {
            error = null;

            var parent = property.Owner?.Parent;
            while (parent != null)
            {
                if (!parent.IsArray && !parent.IsArrayElement &&
                    TryInvokeActionOnTarget(expression, parent.GetValue(targetIndex), property, targetIndex, out _))
                {
                    return true;
                }

                parent = parent.Parent;
            }

            error = CreateActionError(expression);
            return false;
        }

        private static bool TryGetContextObject(ContextExpression expression, TriProperty property, int targetIndex,
            out object target, out string error)
        {
            error = null;
            target = null;

            switch (expression.ContextKind)
            {
                case ContextKind.Owner:
                    target = property.Owner?.GetValue(targetIndex);
                    break;
                case ContextKind.Parent:
                    target = GetParentValue(property, targetIndex);
                    break;
                case ContextKind.Root:
                    target = property.PropertyTree.RootProperty.GetValue(targetIndex);
                    break;
                case ContextKind.Ancestor:
                    target = GetAncestorValue(property, targetIndex, expression.AncestorType);
                    break;
            }

            if (target != null)
            {
                return true;
            }

            error = $"Context expression '{expression.Source}' could not resolve context '{expression.ContextKind}'.";
            return false;
        }

        private static bool TryGetValueOnTarget<T>(ContextExpression expression, object target, TriProperty property,
            int targetIndex, out T value, out string error)
        {
            value = default;
            error = null;

            if (target == null)
            {
                return false;
            }

            var targetType = target.GetType();

            if (!expression.IsMethodCall)
            {
                foreach (var fieldInfo in targetType.GetFields(InstanceFlags))
                {
                    if (fieldInfo.Name == expression.MemberName && IsAssignableMemberType<T>(fieldInfo.FieldType))
                    {
                        value = (T) fieldInfo.GetValue(target);
                        return true;
                    }
                }

                foreach (var propertyInfo in targetType.GetProperties(InstanceFlags))
                {
                    if (propertyInfo.Name == expression.MemberName &&
                        propertyInfo.CanRead &&
                        IsAssignableMemberType<T>(propertyInfo.PropertyType))
                    {
                        value = (T) propertyInfo.GetValue(target);
                        return true;
                    }
                }
            }

            foreach (var methodInfo in targetType.GetMethods(InstanceFlags))
            {
                if (methodInfo.Name == expression.MemberName &&
                    IsAssignableMemberType<T>(methodInfo.ReturnType) &&
                    TryBuildArguments(expression, methodInfo.GetParameters(), property, targetIndex, out var arguments))
                {
                    value = (T) methodInfo.Invoke(target, arguments);
                    return true;
                }
            }

            error = CreateValueError<T>(expression);
            return false;
        }

        private static bool TryInvokeActionOnTarget(ContextExpression expression, object target, TriProperty property,
            int targetIndex, out string error)
        {
            error = null;

            if (target == null)
            {
                return false;
            }

            var targetType = target.GetType();
            foreach (var methodInfo in targetType.GetMethods(InstanceFlags))
            {
                if (methodInfo.Name == expression.MemberName &&
                    methodInfo.ReturnType == typeof(void) &&
                    TryBuildArguments(expression, methodInfo.GetParameters(), property, targetIndex, out var arguments))
                {
                    methodInfo.Invoke(target, arguments);
                    return true;
                }
            }

            error = CreateActionError(expression);
            return false;
        }

        private static bool TryBuildArguments(ContextExpression expression, ParameterInfo[] parameterInfos,
            TriProperty property, int targetIndex, out object[] arguments)
        {
            arguments = null;

            if (!expression.IsMethodCall && parameterInfos.Length != 0)
            {
                return false;
            }

            if (expression.Arguments.Length != parameterInfos.Length)
            {
                return false;
            }

            arguments = new object[expression.Arguments.Length];
            for (var i = 0; i < expression.Arguments.Length; i++)
            {
                if (!TryResolveArgument(expression.Arguments[i], property, targetIndex, out var argument))
                {
                    return false;
                }

                if (!CanAssignArgument(argument, parameterInfos[i].ParameterType))
                {
                    return false;
                }

                arguments[i] = argument;
            }

            return true;
        }

        private static bool TryResolveArgument(string token, TriProperty property, int targetIndex, out object value)
        {
            switch (token)
            {
                case "this":
                case "owner":
                    value = GetThisValue(property, targetIndex);
                    return true;
                case "value":
                    value = property.GetValue(targetIndex);
                    return true;
                case "parent":
                    value = GetParentValue(property, targetIndex);
                    return true;
                case "root":
                    value = property.PropertyTree.RootProperty.GetValue(targetIndex);
                    return true;
                case "index":
                    value = GetIndex(property);
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        private static object GetThisValue(TriProperty property, int targetIndex)
        {
            if (property.IsArrayElement)
            {
                return property.GetValue(targetIndex);
            }

            return property.Owner?.GetValue(targetIndex) ?? property.GetValue(targetIndex);
        }

        private static object GetParentValue(TriProperty property, int targetIndex)
        {
            var parent = property.Owner?.Parent;
            while (parent != null && (parent.IsArray || parent.IsArrayElement))
            {
                parent = parent.Parent;
            }

            return parent?.GetValue(targetIndex);
        }

        private static object GetAncestorValue(TriProperty property, int targetIndex, Type ancestorType)
        {
            var parent = property.Owner?.Parent;
            while (parent != null)
            {
                if (!parent.IsArray && !parent.IsArrayElement)
                {
                    var value = parent.GetValue(targetIndex);
                    if (value != null && ancestorType.IsInstanceOfType(value))
                    {
                        return value;
                    }
                }

                parent = parent.Parent;
            }

            return null;
        }

        private static int GetIndex(TriProperty property)
        {
            if (property.IsArrayElement)
            {
                return property.IndexInArray;
            }

            if (property.Owner != null && property.Owner.IsArrayElement)
            {
                return property.Owner.IndexInArray;
            }

            return -1;
        }

        private static bool CanAssignArgument(object argument, Type parameterType)
        {
            if (argument == null)
            {
                return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
            }

            return parameterType.IsInstanceOfType(argument);
        }

        private static bool IsAssignableMemberType<T>(Type memberType)
        {
            return memberType != typeof(void) && typeof(T).IsAssignableFrom(memberType);
        }

        private static string CreateValueError<T>(ContextExpression expression)
        {
            return $"Context expression '{expression.Source}' could not find member '{expression.MemberName}' with return type '{typeof(T).Name}'. " +
                   "Check that the member exists on the selected context, returns the expected type, and has compatible parameters.";
        }

        private static string CreateActionError(ContextExpression expression)
        {
            return $"Context action expression '{expression.Source}' could not find method '{expression.MemberName}'. " +
                   "Check that the method exists on the selected context, returns void, and has compatible parameters.";
        }
    }
}
