using System.Text.Json.Serialization;
using Taskmetry.Models;

namespace Taskmetry.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class TaskmetryJsonContext : JsonSerializerContext;
