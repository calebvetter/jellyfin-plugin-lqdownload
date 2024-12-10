using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LQDownload;

/// <summary>
/// Provides extension methods for logging functionality.
/// </summary>
public static class LoggerExtensions {
	/// <summary>
	/// Logs all fields and their values for the specified object in a single JSON-like output.
	/// </summary>
	/// <typeparam name="T">The type of the object to log.</typeparam>
	/// <param name="logger">The logger instance used to log the fields.</param>
	/// <param name="obj">The object whose fields will be logged.</param>
	/// <param name="logLevel">
	/// The logging level to use when logging the fields. Defaults to <see cref="LogLevel.Information"/>.
	/// </param>
	public static void LogObjectFields<T>(this ILogger logger, T obj, LogLevel logLevel = LogLevel.Information) {
		if (obj == null) {
			logger.Log(logLevel, "Object is null.");
			return;
		}

		var type = obj.GetType();
		logger.Log(logLevel, "Logging fields and properties for object of type: {TypeName}", type.FullName);

		var data = new Dictionary<string, object?>();

		// Get fields (excluding backing fields)
		var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
										 .Where(f => !f.Name.Contains("BackingField", StringComparison.Ordinal));
		foreach (var field in fields) {
			try {
				var fieldValue = field.GetValue(obj);
				data[field.Name] = FormatValueForLogging(fieldValue);
			}
			catch (Exception ex) {
				data[field.Name] = $"Error retrieving value: {ex.Message}";
			}
		}

		// Get properties
		var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		foreach (var property in properties) {
			try {
				if (property.GetIndexParameters().Length == 0) // Exclude indexers
				{
					var propertyValue = property.GetValue(obj);
					data[property.Name] = FormatValueForLogging(propertyValue);
				}
			}
			catch (Exception ex) {
				data[property.Name] = $"Error retrieving value: {ex.Message}";
			}
		}

		// Convert to JSON-like format
		var jsonLikeOutput = BuildJsonLikeString(data);

		logger.Log(logLevel, "Fields and properties for {TypeName}: {JsonOutput}", type.FullName, jsonLikeOutput);
	}

	private static string FormatValueForLogging(object? value) {
		if (value == null) {
			return "null";
		}

		if (value.GetType().IsEnum) {
			return $"{value.GetType().Name}.{value}";
		}

		if (value is IEnumerable<object> list) {
			return "[" + string.Join(", ", list.Select(FormatValueForLogging)) + "]";
		}

		return value.ToString() ?? "null";
	}

	private static string BuildJsonLikeString(Dictionary<string, object?> data) {
		var jsonLines = data.Select(kvp => $"  \"{kvp.Key}\": {kvp.Value}");
		return "{\n" + string.Join(",\n", jsonLines) + "\n}";
	}
}
