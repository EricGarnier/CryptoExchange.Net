﻿using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json;
using CryptoExchange.Net.Attributes;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace CryptoExchange.Net.Converters.SystemTextJson
{
    /// <summary>
    /// Converter for arrays to objects. Can deserialize data like [0.1, 0.2, "test"] to an object. Mapping is done by marking the class with [JsonConverter(typeof(ArrayConverter))] and the properties
    /// with [ArrayProperty(x)] where x is the index of the property in the array
    /// </summary>
#if NET5_0_OR_GREATER
    public class ArrayConverter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TContext> : JsonConverter<T> where T : new() where TContext: JsonSerializerContext
# else
    public class ArrayConverter<T, TContext> : JsonConverter<T> where T : new() where TContext: JsonSerializerContext
#endif
    {
        private static readonly ConcurrentDictionary<Type, List<ArrayPropertyInfo>> _typeAttributesCache = new ConcurrentDictionary<Type, List<ArrayPropertyInfo>>();
        private static readonly ConcurrentDictionary<JsonConverter, JsonSerializerOptions> _converterOptionsCache = new ConcurrentDictionary<JsonConverter, JsonSerializerOptions>();

        /// <inheritdoc />
#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL3050:RequiresUnreferencedCode", Justification = "JsonSerializerOptions provided here has TypeInfoResolver set")]
        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "JsonSerializerOptions provided here has TypeInfoResolver set")]
#endif
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();

            var valueType = typeof(T);
            if (!_typeAttributesCache.TryGetValue(valueType, out var typeAttributes))
                typeAttributes = CacheTypeAttributes(valueType);

            var ordered = typeAttributes.Where(x => x.ArrayProperty != null).OrderBy(p => p.ArrayProperty.Index);
            var last = -1;
            foreach (var prop in ordered)
            {
                if (prop.ArrayProperty.Index == last)
                    continue;

                while (prop.ArrayProperty.Index != last + 1)
                {
                    writer.WriteNullValue();
                    last += 1;
                }

                last = prop.ArrayProperty.Index;

                var objValue = prop.PropertyInfo.GetValue(value);
                if (objValue == null)
                {
                    writer.WriteNullValue();
                    continue;
                }

                JsonSerializerOptions? typeOptions = null;
                if (prop.JsonConverter != null)
                {
                    typeOptions = new JsonSerializerOptions
                    {
                        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                        PropertyNameCaseInsensitive = false,
                        TypeInfoResolver = (TContext)Activator.CreateInstance(typeof(TContext))!,
                    };
                    typeOptions.Converters.Add(prop.JsonConverter);
                }

                if (prop.JsonConverter == null && IsSimple(prop.PropertyInfo.PropertyType))
                {
                    if (prop.TargetType == typeof(string))
                        writer.WriteStringValue(Convert.ToString(objValue, CultureInfo.InvariantCulture));
                    else if (prop.TargetType == typeof(bool))
                        writer.WriteBooleanValue((bool)objValue);
                    else
                        writer.WriteRawValue(Convert.ToString(objValue, CultureInfo.InvariantCulture)!);
                }
                else
                {
                    JsonSerializer.Serialize(writer, objValue, typeOptions ?? options);
                }
            }

            writer.WriteEndArray();
        }

        /// <inheritdoc />
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return default;

            var result = Activator.CreateInstance(typeof(T))!;
            return (T)ParseObject(ref reader, result, typeof(T), options);
        }

        private static bool IsSimple(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                // nullable type, check if the nested type is simple.
                return IsSimple(type.GetGenericArguments()[0]);
            }
            return type.IsPrimitive
              || type.IsEnum
              || type == typeof(string)
              || type == typeof(decimal);
        }

#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL3050:RequiresUnreferencedCode", Justification = "JsonSerializerOptions provided here has TypeInfoResolver set")]
        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "JsonSerializerOptions provided here has TypeInfoResolver set")]
        private static List<ArrayPropertyInfo> CacheTypeAttributes([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
#else
        private static List<ArrayPropertyInfo> CacheTypeAttributes(Type type)
#endif
        {
            var attributes = new List<ArrayPropertyInfo>();
            var properties = type.GetProperties();
            foreach (var property in properties)
            {
                var att = property.GetCustomAttribute<ArrayPropertyAttribute>();
                if (att == null)
                    continue;

                var converterType = property.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType ?? property.PropertyType.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType;
                attributes.Add(new ArrayPropertyInfo
                {
                    ArrayProperty = att,
                    PropertyInfo = property,
                    DefaultDeserialization = property.GetCustomAttribute<CryptoExchange.Net.Attributes.JsonConversionAttribute>() != null,
                    JsonConverter = converterType == null ? null : (JsonConverter)Activator.CreateInstance(converterType)!,
                    TargetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType
                });
            }

            _typeAttributesCache.TryAdd(type, attributes);
            return attributes;
        }


#if NET5_0_OR_GREATER
        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL3050:RequiresUnreferencedCode", Justification = "JsonSerializerOptions provided here has TypeInfoResolver set")]
        [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode", Justification = "JsonSerializerOptions provided here has TypeInfoResolver set")]
        private static object ParseObject(ref Utf8JsonReader reader, object result, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type objectType, JsonSerializerOptions options)
#else
        private static object ParseObject(ref Utf8JsonReader reader, object result, Type objectType, JsonSerializerOptions options)
#endif
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new Exception("Not an array");

            if (!_typeAttributesCache.TryGetValue(objectType, out var attributes))
                attributes = CacheTypeAttributes(objectType);

            int index = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                var indexAttributes = attributes.Where(a => a.ArrayProperty.Index == index);
                if (!indexAttributes.Any())
                {
                    index++;
                    continue;
                }

                foreach (var attribute in indexAttributes)
                {
                    var targetType = attribute.TargetType;
                    object? value = null;
                    if (attribute.JsonConverter != null)
                    {
                        if (!_converterOptionsCache.TryGetValue(attribute.JsonConverter, out var newOptions))
                        {                            
                            newOptions = new JsonSerializerOptions
                            {
                                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                                PropertyNameCaseInsensitive = false,
                                Converters = { attribute.JsonConverter },
                                TypeInfoResolver = options.TypeInfoResolver,
                            };
                            _converterOptionsCache.TryAdd(attribute.JsonConverter, newOptions);
                        }

                        value = JsonDocument.ParseValue(ref reader).Deserialize(attribute.PropertyInfo.PropertyType, newOptions);
                    }
                    else if (attribute.DefaultDeserialization)
                    {
                        // Use default deserialization
                        value = JsonDocument.ParseValue(ref reader).Deserialize(attribute.PropertyInfo.PropertyType, SerializerOptions.WithConverters((TContext)Activator.CreateInstance(typeof(TContext))!));
                    }
                    else
                    {
                        value = reader.TokenType switch
                        {
                            JsonTokenType.Null => null,
                            JsonTokenType.False => false,
                            JsonTokenType.True => true,
                            JsonTokenType.String => reader.GetString(),
                            JsonTokenType.Number => reader.GetDecimal(),
                            JsonTokenType.StartObject => JsonSerializer.Deserialize(ref reader, attribute.TargetType, options),
                            _ => throw new NotImplementedException($"Array deserialization of type {reader.TokenType} not supported"),
                        };
                    }

                    if (targetType.IsAssignableFrom(value?.GetType()))
                        attribute.PropertyInfo.SetValue(result, value);
                    else
                        attribute.PropertyInfo.SetValue(result, value == null ? null : Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture));
                }

                index++;
            }

            return result;
        }

        private class ArrayPropertyInfo
        {
            public PropertyInfo PropertyInfo { get; set; } = null!;
            public ArrayPropertyAttribute ArrayProperty { get; set; } = null!;
            public JsonConverter? JsonConverter { get; set; }
            public bool DefaultDeserialization { get; set; }
            public Type TargetType { get; set; } = null!;
        }
    }
}
