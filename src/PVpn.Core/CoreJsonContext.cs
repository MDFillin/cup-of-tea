using System.Text.Json.Serialization;
using PVpn.Core.Profiles;

namespace PVpn.Core;

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProfileData))]
[JsonSerializable(typeof(Profile))]
internal partial class CoreJsonContext : JsonSerializerContext;
