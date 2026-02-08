using System;
using System.Collections.Generic;
using System.Globalization;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// Provides a deterministic sequence of answers that can be used to drive the interactive shell in tests or scripted scenarios.
    /// </summary>
    public sealed class InteractiveScript
    {
        private readonly Queue<object?> _responses = new();

        /// <summary>
        /// Queues a response that will be consumed by the next prompt invocation.
        /// </summary>
        /// <param name="value">Value to return when the interactive shell requests input.</param>
        /// <returns>The current script instance for fluent chaining.</returns>
        public InteractiveScript Enqueue(object? value)
        {
            _responses.Enqueue(value);
            return this;
        }

        /// <summary>
        /// Attempts to dequeue a value of the specified type.
        /// </summary>
        /// <typeparam name="T">Expected type.</typeparam>
        /// <param name="value">Output value when available.</param>
        /// <returns>True when the script provided a value; otherwise, false.</returns>
        public bool TryDequeue<T>(out T value)
        {
            if (_responses.Count == 0)
            {
                value = default!;
                return false;
            }

            var next = _responses.Dequeue();
            if (TryConvert(next, out value))
            {
                return true;
            }

            throw new InvalidOperationException($"Scripted response '{next}' cannot be converted to {typeof(T)}.");
        }

        /// <summary>
        /// Attempts to peek at the next value without removing it from the queue.
        /// </summary>
        /// <typeparam name="T">Expected type.</typeparam>
        /// <param name="value">Output value when available.</param>
        /// <returns>True when the script can expose the next value as the requested type.</returns>
        public bool TryPeek<T>(out T value)
        {
            if (_responses.Count == 0)
            {
                value = default!;
                return false;
            }

            var next = _responses.Peek();
            if (TryConvert(next, out value))
            {
                return true;
            }

            value = default!;
            return false;
        }

        private static bool TryConvert<T>(object? input, out T value)
        {
            if (input is T direct)
            {
                value = direct;
                return true;
            }

            if (input is null)
            {
                value = default!;
                return !typeof(T).IsValueType;
            }

            if (typeof(T).IsEnum)
            {
                if (TryConvertEnum(input, out value))
                {
                    return true;
                }

                value = default!;
                return false;
            }

            if (typeof(T) == typeof(string))
            {
                value = (T)(object)input.ToString()!;
                return true;
            }

            if (typeof(T) == typeof(bool))
            {
                switch (input)
                {
                    case bool flag:
                        value = (T)(object)flag;
                        return true;
                    case string text when bool.TryParse(text, out var parsedBool):
                        value = (T)(object)parsedBool;
                        return true;
                    case int numeric:
                        value = (T)(object)(numeric != 0);
                        return true;
                }
            }

            if (input is IConvertible)
            {
                value = (T)Convert.ChangeType(input, typeof(T));
                return true;
            }

            value = default!;
            return false;
        }

        private static bool TryConvertEnum<T>(object? input, out T value)
        {
            value = default!;
            var enumType = typeof(T);
            var values = Enum.GetValues(enumType);

            if (input is string text)
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    if (index >= 0 && index < values.Length)
                    {
                        value = (T)values.GetValue(index)!;
                        return true;
                    }

                    return false;
                }

                if (Enum.TryParse(enumType, text, true, out var parsed) && IsDefinedOrFlagsValue(enumType, parsed))
                {
                    value = (T)parsed;
                    return true;
                }

                return false;
            }

            if (input is int enumIndex)
            {
                if (enumIndex >= 0 && enumIndex < values.Length)
                {
                    value = (T)values.GetValue(enumIndex)!;
                    return true;
                }

                return false;
            }

            if (input is IConvertible convertible)
            {
                try
                {
                    var underlyingType = Enum.GetUnderlyingType(enumType);
                    var numeric = Convert.ChangeType(convertible, underlyingType, CultureInfo.InvariantCulture);
                    if (numeric is not null && IsDefinedOrFlagsValue(enumType, Enum.ToObject(enumType, numeric)))
                    {
                        value = (T)Enum.ToObject(enumType, numeric);
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool IsDefinedOrFlagsValue(Type enumType, object value)
        {
            if (Enum.IsDefined(enumType, value))
            {
                return true;
            }

            var isFlags = enumType.IsDefined(typeof(FlagsAttribute), inherit: false);
            if (!isFlags)
            {
                return false;
            }

            var inputValue = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            ulong definedMask = 0;
            foreach (var item in Enum.GetValues(enumType))
            {
                definedMask |= Convert.ToUInt64(item, CultureInfo.InvariantCulture);
            }

            return (inputValue & ~definedMask) == 0;
        }
    }
}
