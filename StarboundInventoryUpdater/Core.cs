namespace StarboundInventoryUpdater;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Win32;

internal class Core {
	// exit codes
	public const int
		EX_NOSTEAM = 2,
		EX_NOGAME = 3,
		EX_FILESLIVE = 4,
		EX_NOPLAYERS = 5,
		EX_BAGTOOBIG = 6,
		EX_UNEXPECTEDFILE = 126,
		EX_INTERNAL = 127,
		EX_EXTERNAL = 128;
	public const int SLOT_COUNT = 120;

	public static readonly string INF = uint.MaxValue.ToString(); // because the starbound devs' json generator outputs INVALID json containing "inf" values

	internal static readonly string[] bagNames = new string[] {
		// vanilla
		"objectBag",
		"materialBag",
		"foodBag",
		"reagentBag",
		"mainBag",
		// bk3k
		"armoryBag",
		"farmBag",
		"vehicleBag",
		"objectBag2",
		"hobbyBag",
	};
	internal static readonly int bagNameLength = bagNames.Select(s => s.Length).Max();

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "entry point")]
	public static void Main(string[] args) {
		string hkcu = Registry.CurrentUser.Name;
		string steamKeyBase = $@"{hkcu}\SOFTWARE\Valve\Steam";

		string? steampath = getRegVal(steamKeyBase, "SteamPath", null) as string;
		int? isInstalled = getRegVal($@"{steamKeyBase}\Apps\211820", "Installed", null) as int?;
		int? isUpdating = getRegVal($@"{steamKeyBase}\Apps\211820", "Updating", null) as int?;
		int? isRunning = getRegVal($@"{steamKeyBase}\Apps\211820", "Running", null) as int?;

		if (steampath is null)
			abort("Cannot locate Steam installation path", EX_NOSTEAM);
		if (isInstalled is null or 0)
			abort("Starbound does not appear to be installed\nYou may need to launch the game once first.", EX_NOGAME);
		if (isUpdating is null or not 0)
			abort("Steam reports that Starbound is currently updating.\nPlease wait until this is done.", EX_FILESLIVE);
		if (isRunning is null or not 0)
			abort("Steam reports that Starbound is currently running.\nPlease close the game first.", EX_FILESLIVE);

		string starbound = joinPath(steampath!, "steamapps", "common", "Starbound");
		string players = joinPath(starbound, "storage", "player");
		string utils = joinPath(starbound, "win32");
		string toJsonExe = joinPath(utils, "dump_versioned_json.exe");
		string fromJsonExe = joinPath(utils, "make_versioned_json.exe");

		// these are full paths
		string[] playerFiles = Directory.GetFiles(players, "*.player", SearchOption.TopDirectoryOnly);

		if (playerFiles.Length < 1)
			abort("No player files found.", EX_NOPLAYERS);

		Stopwatch timer = new();

		foreach (string rawFile in playerFiles) {
			timer.Restart();
			string id = Path.GetFileNameWithoutExtension(rawFile);
			string jsonFile = Path.GetTempFileName();
			string tempFile = Path.GetTempFileName();
			log($"< {id}");
			try {

				int exit = run(toJsonExe, rawFile, jsonFile);
				if (exit is not 0) {
					log($"# {exit}");
					abort("Failed to parse player file.", EX_EXTERNAL + exit);
				}
				if (!File.Exists(jsonFile)) {
					abort($"! {jsonFile}", EX_UNEXPECTEDFILE);
				}

				string json = jsonFix(File.ReadAllText(jsonFile), "inf", INF);
				JsonNode root = JsonNode.Parse(json, null, new() {
					AllowTrailingCommas = true,
					CommentHandling = JsonCommentHandling.Skip,
				})!;

				JsonNode identity = root["content"]!["identity"]!;
				log($"= {identity["name"]}, {identity["gender"]} {identity["species"]}");

				JsonNode bags = root["content"]!["inventory"]!["itemBags"]!;

				foreach (string bagName in bagNames) {
					JsonArray bag = bags[bagName] as JsonArray ?? new JsonArray();
					log($"? {bagName.PadRight(bagNameLength)} = {bag.Count}/{SLOT_COUNT}");

					if (bag.Count > SLOT_COUNT)
						abort("Too many item slots - cannot reduce slot count without potential item loss.", EX_BAGTOOBIG);

					while (bag.Count < SLOT_COUNT)
						bag.Add(null);

					bags[bagName] = bag;
				}

				json = root.ToJsonString(new() {
					WriteIndented = false,
					AllowTrailingCommas = false,
				});
				File.WriteAllText(jsonFile, json);

				exit = run(fromJsonExe, jsonFile, tempFile);
				if (exit is 0) {
					log($"> {id}");
					File.Move(tempFile, rawFile, true);
				}
				else {
					abort($"#{exit}", EX_EXTERNAL + exit);
				}
			}
			catch (Exception e) {
				abort(e.ToString(), EX_INTERNAL);
			}
#if !DEBUG
			finally {
				log($": {tempFile}");
				File.Delete(tempFile);
				log($": {jsonFile}");
				File.Delete(jsonFile);
			}
#endif
			timer.Stop();
			log($"+ {timer.ElapsedMilliseconds}ms");
			log("");
		}

		abort("Player files updated.");
	}

	internal static string jsonFix(in string src, in string find, in string replace) {
		bool quoted = false;
		int findLength = find.Length;
		StringBuilder json = new();

		log($"| {src.Length},{findLength}:{find},{replace.Length}:{replace}");

		for (int i = 0; i < src.Length; ++i) {
			if (quoted && src[i] == '\\') {
				json.Append(src[i]);
				json.Append(src[++i]);
			}
			else if (src[i] == '"') {
				quoted = !quoted;
				json.Append(src[i]);
			}
			else if (!quoted && i + findLength < src.Length && src.Substring(i, findLength) == find) {
				json.Append(replace);
				i += findLength - 1;
			}
			else {
				json.Append(src[i]);
			}
		}

#if DEBUG
		string dbg = Path.GetTempFileName();
		File.WriteAllText(dbg, json.ToString());
		log($"& {dbg}");
#endif

		return json.ToString();
	}

	internal static int run(string path, params string[] args) {
		log($"^ \"{path}\"");
		foreach (string arg in args)
			log($"  \"{arg}\"");
		using Process child = Process.Start(path, args);
		child.WaitForExit();
		log($"$ {(child.ExitTime - child.StartTime).TotalMilliseconds}ms (as {child.ExitCode})");
		return child.ExitCode;
	}

	internal static string joinPath(params string[] parts)
		=> Path.GetFullPath(Path.Combine(parts));

	internal static object? getRegVal(string key, string name, object? fallback = null) {
		object? value = Registry.GetValue(key, name, fallback);
		if (value is null)
			log($@"{key}\{name} is null");
		else
			log($@"{key}\{name}={value.GetType().Name.ToLower()}:{value}");
		return value;
	}

	internal static void log(string message)
		=> Console.WriteLine(message);

	internal static void abort(string message, int code = 0) {
		log("");
		log(message);
		log("");
		if (code is not 0)
			Console.WriteLine($"Aborting{(code >= EX_INTERNAL ? ", please send this log to the developer" : "")}.");
		Console.WriteLine("Press any key to close this window.");
		Console.ReadKey(true);
		Environment.Exit(code);
	}
}
