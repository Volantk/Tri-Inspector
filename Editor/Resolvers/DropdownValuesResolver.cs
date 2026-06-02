using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;

namespace TriInspector.Resolvers
{
    public class DropdownValuesResolver<T>
    {
        [CanBeNull] private ValueResolver<IEnumerable<TriDropdownItem<T>>> _itemsResolver;
        [CanBeNull] private ValueResolver<IEnumerable<T>> _valuesResolver;

        [PublicAPI]
        public static DropdownValuesResolver<T> Resolve(TriPropertyDefinition propertyDefinition, string expression)
        {
            var valuesResolver = ValueResolver.Resolve<IEnumerable<T>>(propertyDefinition, expression);
            var valuesHasError = valuesResolver.TryGetErrorString(out _);

            if (!valuesHasError && !IsContextExpression(expression))
            {
                return new DropdownValuesResolver<T>
                {
                    _valuesResolver = valuesResolver,
                };
            }

            var itemsResolver = ValueResolver.Resolve<IEnumerable<TriDropdownItem<T>>>(propertyDefinition, expression);
            var itemsHasError = itemsResolver.TryGetErrorString(out _);

            if (IsContextExpression(expression))
            {
                if (valuesHasError && itemsHasError)
                {
                    return new DropdownValuesResolver<T>
                    {
                        _itemsResolver = itemsResolver,
                    };
                }

                return new DropdownValuesResolver<T>
                {
                    _valuesResolver = valuesHasError ? null : valuesResolver,
                    _itemsResolver = itemsHasError ? null : itemsResolver,
                };
            }

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
            return ValueResolver.TryGetErrorString(_valuesResolver, _itemsResolver, out error);
        }

        [PublicAPI]
        public IEnumerable<ITriDropdownItem> GetDropdownItems(TriProperty property)
        {
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
    }
}