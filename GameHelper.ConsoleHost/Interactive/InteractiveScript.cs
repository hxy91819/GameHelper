using System;
using System.Collections.Generic;

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
                if (input is string enumName && Enum.TryParse(typeof(T), enumName, true, out var parsed))
                {
                    value = (T)parsed;
                    return true;
                }

                if (input is int enumIndex)
                {
                    var values = Enum.GetValues(typeof(T));
                    if (enumIndex >= 0 && enumIndex < values.Length)
                    {
                        value = (T)values.GetValue(enumIndex)!;
                        return true;
                    }
                }
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
    }
}
