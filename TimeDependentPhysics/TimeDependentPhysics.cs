using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security;
using System.Security.Permissions;
using UnityEngine;


// If there are errors in the above using statements, restore the NuGet packages:
// 1. Left-click on the TimeDependentPhysics Project in the Solution Explorer (not TimeDependentPhysics.cs)
// 2. In the pop-up context menu, click on "Manage NuGet Packages..."
// 3. In the top-right corner of the NuGet Package Manager, click "restore"


// You can add references to another BepInEx plugin:
// 1. Left-click on the TimeDependentPhysics Project's references in the Solution Explorer
// 2. Select the "Add Reference..." context menu option.
// 3. Expand the "Assemblies" tab group, and select the "Extensions" tab
// 4. Choose your assemblies then select "Ok"
// 5. Be sure to select each of the added references in the solution explorer,
//    then in the properties window, set "Copy Local" to false.



// This is the major & minor version with an asterisk (*) appended to auto increment numbers.
[assembly: AssemblyVersion(COM3D2.TimeDependentPhysics.PluginInfo.PLUGIN_VERSION + ".*")]
[assembly: AssemblyFileVersion(COM3D2.TimeDependentPhysics.PluginInfo.PLUGIN_VERSION)]

// These two lines tell your plugin to not give a flying fuck about accessing private variables/classes whatever.
// It requires a publicized stubb of the library with those private objects though. 
[module: UnverifiableCode]
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]


namespace COM3D2.TimeDependentPhysics
{
	public static class PluginInfo
	{
		// The name of this assembly.
		public const string PLUGIN_GUID = "COM3D2.TimeDependentPhysics";
		// The name of this plugin.
		public const string PLUGIN_NAME = "TimeDependentPhysics";
		// The version of this plugin.
		public const string PLUGIN_VERSION = "0.5";
	}
}



namespace COM3D2.TimeDependentPhysics
{
	// This is the metadata set for your plugin.
	[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
	public sealed class TimeDependentPhysics : BaseUnityPlugin
	{
		// Static saving of the main instance. (Singleton design pattern)
		// This makes it easier to run stuff like coroutines from static methods or accessing non-static vars.
		public static TimeDependentPhysics Instance { get; private set; }

		// Static property for the logger so you can log from other classes.
		internal static new ManualLogSource Logger => Instance?._Logger;
		private ManualLogSource _Logger => base.Logger;

		// Config entry variable. You set your configs to this.
		internal static ConfigEntry<bool> PluginEnabled;
		internal static ConfigEntry<bool> PatchingEnabled;
		internal static ConfigEntry<float> SimulateFps;
		internal static ConfigEntry<float> MinimumFps;

		private void Awake()
		{
			// Useful for engaging coroutines or accessing non-static variables. Completely optional though.
			Instance = this;

			PluginEnabled = Config.Bind("Plugin", "PluginEnabled", true, 
				"When disabled, effectivly removes the effects of the plugin.");

			PatchingEnabled = Config.Bind("Plugin", "PatchingEnabled", true, 
				"When disabled, will not attempt to apply Harmony patches. (Disables plugin, requires restart)");

			SimulateFps = Config.Bind("Simulation", "SimulateFps", 60f, new ConfigDescription(
				"Simulate physics as if the game is always running at this framerate.",
				new AcceptableValueRange<float>(10f, 120f)
			));

			MinimumFps = Config.Bind("Simulation", "MinimumFps", 30f, new ConfigDescription(
				"The minimum frames per second for which time-correction will occur.",
				new AcceptableValueRange<float>(10f, 120f)
			));

			if (PatchingEnabled.Value)
			{
				// Installs the patches in the TimeDependentPhysics class.
				Harmony.CreateAndPatchAll(typeof(TimeDependentPhysics));

				Logger.LogInfo("Patching Complete");
			}
			else
			{
				Logger.LogInfo("Skip Patching");
			}
			
		}

		private void Update()
		{ }

		[HarmonyTranspiler, HarmonyPatch(typeof(jiggleBone), nameof(jiggleBone.LateUpdateSelf))]
		static IEnumerable<CodeInstruction> LateUpdateSelf_Transpiler(IEnumerable<CodeInstruction> instrs)
		{
			var method_LateUpdateSelf = AccessTools.Method(typeof(jiggleBone), nameof(jiggleBone.LateUpdateSelf));
			var transpileException = new InvalidOperationException("Could not transpile jiggleBone.LateUpdateSelf");

			var field_BlendValueON = AccessTools.DeclaredField(typeof(jiggleBone), nameof(jiggleBone.BlendValueON));
			var smethod_GetTimeFactor = AccessTools.Method(typeof(TimeDependentPhysics), nameof(TimeDependentPhysics.GetTimeFactor));
			var local_num5 = 21; // Not used until after the if block, so use this for now

			var transpileHead = new CodeMatcher(instrs);

			CodeMatch[] match_BlendIfCheck =
			{
				// if (0.0 < (double) this.BlendValueON)
				new(OpCodes.Ldc_R4 ),
				new(OpCodes.Ldarg_0),
				new(OpCodes.Ldfld  , field_BlendValueON),
				new(OpCodes.Bge_Un )
			};
			transpileHead.MatchForward(useEnd: true, match_BlendIfCheck);
			if (transpileHead.ReportFailure(method_LateUpdateSelf, Logger.LogError)) throw transpileException;
			Logger.LogDebug($"match_BlendIfCheck @ {transpileHead.Pos} with {transpileHead.Opcode} {transpileHead.Operand}");
			transpileHead.Advance(1);

			CodeInstruction[] code_DeclareTimeFactor =
			{
				// float timeFactor = GetTimeFactor();
				new(OpCodes.Call   , smethod_GetTimeFactor),
				new(OpCodes.Stloc_S, local_num5),
			};
			transpileHead.Insert(code_DeclareTimeFactor);

			// --- Convert  ---
			// this.vel.x += ???;
			// this.vel.y += ???;
			// this.vel.z += ???;
			// -----  To  -----
			// this.vel.x += ??? * timeFactor;
			// this.vel.y += ??? * timeFactor;
			// this.vel.z += ??? * timeFactor;
			CodeInstruction[] code_MulTimeFactor =
			{
				// * timeFactor;
				new(OpCodes.Ldloc_S, local_num5),
				new(OpCodes.Mul    ),
			};
			var field_Vector3_x = AccessTools.Field(typeof(Vector3), nameof(Vector3.x));
			var field_Vector3_y = AccessTools.Field(typeof(Vector3), nameof(Vector3.y));
			var field_Vector3_z = AccessTools.Field(typeof(Vector3), nameof(Vector3.z));
			FieldInfo[] floatFields = { field_Vector3_x, field_Vector3_y, field_Vector3_z };
			foreach (var floatField in floatFields)
			{
				CodeMatch[] match_AddToField =
				{
					// Vector.? += ?
					new(OpCodes.Add),
					new(OpCodes.Stfld, floatField),
				};
				transpileHead.MatchForward(useEnd: false, match_AddToField);
				if (transpileHead.ReportFailure(method_LateUpdateSelf, Logger.LogError)) throw transpileException;
				Logger.LogDebug($"match_AddToField {floatField.Name} @ {transpileHead.Pos} with {transpileHead.Opcode} {transpileHead.Operand}");
				transpileHead.Insert(code_MulTimeFactor);
			}


			// --- Convert  ---
			// this.dynamicPos += this.force;
			// this.dynamicPos += this.vel;
			// -----  To  -----
			// this.dynamicPos += this.force * timeFactor;
			// this.dynamicPos += this.vel   * timeFactor;
			var smethod_V_Add = AccessTools.Method(typeof(Vector3), "op_Addition");
			var smethod_V_Mul = AccessTools.Method(typeof(Vector3), "op_Multiply", new Type[] { typeof(Vector3), typeof(float) });
			CodeInstruction[] code_VMulTimeFactor =
			{
				// ??? * timeFactor
				new(OpCodes.Ldloc_S, local_num5),
				new(OpCodes.Call   , smethod_V_Mul),
			};
			var field_force      = AccessTools.Field(typeof(jiggleBone), nameof(jiggleBone.force     ));
			var field_vel        = AccessTools.Field(typeof(jiggleBone), nameof(jiggleBone.vel       ));
			var field_dynamicPos = AccessTools.Field(typeof(jiggleBone), nameof(jiggleBone.dynamicPos));
			FieldInfo[] vectorFields = { field_force, field_vel };
			foreach (var vectorField in vectorFields)
			{
				CodeMatch[] match_AddToVector =
				{
					// this.dynamicPos += ???
					new(OpCodes.Ldfld, vectorField),
					new(OpCodes.Call , smethod_V_Add),
					new(OpCodes.Stfld, field_dynamicPos),
				};
				transpileHead.MatchForward(useEnd: false, match_AddToVector);
				if (transpileHead.ReportFailure(method_LateUpdateSelf, Logger.LogError)) throw transpileException;
				Logger.LogDebug($"match_AddToVector {vectorField.Name} += @ {transpileHead.Pos} with {transpileHead.Opcode} {transpileHead.Operand}");
				transpileHead.Advance(1); 
				transpileHead.Insert(code_VMulTimeFactor);
			}


			return transpileHead.InstructionEnumeration();
		}

		static float GetTimeFactor()
		{
			if (!PluginEnabled.Value) return 1;

			// Don't extrapolate any further if deltaTime is greater than MinimumFPS's rate.
			float maxDeltaTime = 1 / Mathf.Max(1, MinimumFps.Value);
			float simulateFPS = Mathf.Max(1, SimulateFps.Value);

			// Calculate time factor relative to what the time would have been if it was running at simulateFPS
			float timeFactor = Mathf.Min(maxDeltaTime, Time.deltaTime) / (1 / simulateFPS);

			return timeFactor;
		}
		
	}
}
