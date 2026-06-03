using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using UnityEngine;

namespace TriInspector.Resolvers
{
    public class DropdownValuesResolver<T>
    {
        [CanBeNull] private ValueResolver<object> _contextResolver;
        [CanBeNull] private ValueResolver<IEnumerable<TriDropdownItem<T>>> _itemsResolver;
        [CanBeNull] private ValueResolver<IEnumerable<T>> _valuesResolver;

        private bool _loggedContextTypeError;

        [PublicAPI]
        public static DropdownValuesResolver<T> Resolve(TriPropertyDefinition propertyDefinition, string expression)
        {
            if (IsContextExpression(expression))
            {
                return new DropdownValuesResolver<T>
                {
                    _contextResolver = ValueResolver.Resolve<object>(propertyDefinition, expression),
                };
            }

            var valuesResolver = ValueResolver.Resolve<IEnumerable<T>>(propertyDefinition, expression);
            if (!valuesResolver.TryGetErrorString(out _))
            {
                return new DropdownValuesResolver<T>
                {
                    _valuesResolver = valuesResolver,
                };
            }

            var itemsResolver = ValueResolver.Resolve<IEnumerable<TriDropdownItem<T>>>(propertyDefinition, expression);

            return new DropdownValuesResolver<T>
            {
                _itemsResolver = itemsResolver,
            };
        }

        private static bool IsContextExpression(string expression)
        {
            return expression != null && expression.StartsWith("@");
        }

        [PublicAPI]
        public bool TryGetErrorString(out string error)
        {
            if (_contextResolver != null)
            {
                return _contextResolver.TryGetErrorString(out error);
            }

            return ValueResolver.TryGetErrorString(_valuesResolver, _itemsResolver, out error);
        }

        [PublicAPI]
        public IEnumerable<ITriDropdownItem> GetDropdownItems(TriProperty property)
        {
            if (_contextResolver != null)
            {
                foreach (var item in GetContextDropdownItems(property))
                {
                    yield return item;
                }

                yield break;
            }

            if (_valuesResolver != null)
            {
                var values = _valuesResolver.GetValue(property, Enumerable.Empty<T>());

                foreach (var value in values)
                {
                    yield return new TriDropdownItem {Text = $"{value}", Value = value,};
                }
            }

            if (_itemsResolver != null)
            {
                var values = _itemsResolver.GetValue(property, Enumerable.Empty<TriDropdownItem<T>>());

                foreach (var value in values)
                {
                    yield return value;
                }
            }
        }

        private IEnumerable<ITriDropdownItem> GetContextDropdownItems(TriProperty property)
        {
            var value = _contextResolver.GetValue(property);
            if (value == null)
            {
                yield break;
            }

            if (value is IEnumerable<TriDropdownItem<T>> typedItems)
            {
                foreach (var item in typedItems)
                {
                    yield return item;
                }

                yield break;
            }

            if (value is IEnumerable<T> typedValues)
            {
                foreach (var item in typedValues)
                {
                    yield return new TriDropdownItem {Text = $"{item}", Value = item,};
                }

                yield break;
            }

            if (!_loggedContextTypeError)
            {
                _loggedContextTypeError = true;
                Debug.LogError($"Context dropdown expression returned '{value.GetType().Name}', but expected " +
                               $"'{typeof(IEnumerable<T>)}' or '{typeof(IEnumerable<TriDropdownItem<T>>)}'.");
            }
        }
    }
}
