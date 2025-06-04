using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace SteamApiPatcher;

public class Patcher
{
    private const string GUID = "com.smrkn.steam-api-patcher";
    private const string NAME = "Steam API Patcher";
    private const string VERSION = "0.1.0";

    public static IEnumerable<string> TargetDLLs { get; } = new[] { "Facepunch.Steamworks.Win64.dll" };

    internal static ManualLogSource Logger;

    private static Stream? _patched;

    static Patcher()
    {
        Logger = BepInEx.Logging.Logger.CreateLogSource(NAME);
        Logger.LogInfo($"Initializing {NAME} v{VERSION}");
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr LoadLibrary(string lpFileName);

    public static void Initialize()
    {
        string steamDllPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "steam_api64_v161.dll");

        Logger.LogInfo("Attempting to patch steam_api64.dll");
        var steamDll = LoadLibrary(steamDllPath);
        if (steamDll == IntPtr.Zero)
        {
            Logger.LogError("Failed to load steam_api64.dll");
        }
        else
        {
            Logger.LogInfo("Newer steam_api64.dll loaded successfully.");
        }
    }

    public static void Patch(ref AssemblyDefinition assembly)
    {
        Logger.LogInfo("Attempting to patch Facepunch.Steamworks.Win64.dll");

        var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Facepunch.Steamworks.Win64.dll");
        _patched = File.Open(path, FileMode.Open, FileAccess.Read);

        AssemblyDefinition replacement = AssemblyDefinition.ReadAssembly(_patched);

        // Eagerly patch Steam DllImport
        // If this function fails, the rest of the patch won't apply and Facepunch will still load like normal.
        PatchSteamImports(replacement);
        
        PatchConnectionManager(replacement);
        PatchSocketManager(replacement);
        PatchSteamUser(replacement);
        PatchConnection(replacement);

        replacement.Name = assembly.Name;

        replacement.Write(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "patchwork.dll"));
        assembly = replacement;
        Logger.LogInfo("Facepunch.Steamworks.Win64.dll patched successfully.");
    }

    static int PatchAllNestedTypes(TypeDefinition type)
    {
        int count = 0;
        foreach (var method in type.Methods)
        {
            var pinvoke = method.PInvokeInfo;
            if (pinvoke == null || pinvoke.Module == null) continue;

            var moduleName = pinvoke.Module?.Name;
            if (string.Equals(moduleName, "steam_api64", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(moduleName, "steam_api64.dll", StringComparison.OrdinalIgnoreCase))
            {
                pinvoke.Module!.Name = "steam_api64_v161";
                count++;
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            count += PatchAllNestedTypes(nested);
        }

        return count;
    }

    private static void PatchSteamImports(AssemblyDefinition assembly)
    {
        var module = assembly.MainModule;

        int patched = 0;

        foreach (var type in module.Types)
        {
            patched += PatchAllNestedTypes(type);
        }

        if (patched > 0)
        {
            Logger.LogInfo("Successfully patched Steam API; now using v161");
        }
        else
        {
            Logger.LogError("Failed to patch Steam API; aborting patching");
            throw new Exception("Failed to patch Steam API");
        }
    }

    private static void PatchConnection(AssemblyDefinition assembly)
    {
        var connection = assembly.MainModule.GetType("Steamworks.Data.Connection");

        if (connection == null)
        {
            Logger.LogError("Failed to find Steamworks.Data.Connection type in assembly.");
            return;
        }

        var originalSendMessage = connection.Methods.First(m =>
            m.Name == "SendMessage"
            && m.Parameters.Count == 3
            && m.Parameters[0].ParameterType.FullName == "System.Byte[]"
            && m.Parameters[1].ParameterType.FullName == "Steamworks.Data.SendType"
            && m.Parameters[2].ParameterType.FullName == "System.UInt16"
        );

        if (originalSendMessage != null)
        {
            Logger.LogInfo("Patching Connection.SendMessage method");

            var sendMessage = new MethodDefinition(
                "SendMessage",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                assembly.MainModule.ImportReference(originalSendMessage.ReturnType)
            );

            // sendMessage.ImplAttributes |= Mono.Cecil.MethodImplAttributes.Unmanaged;

            var byteArrayType = originalSendMessage.Parameters[0].ParameterType;
            var sendType = originalSendMessage.Parameters[1].ParameterType;

            sendMessage.Parameters.Add(new ParameterDefinition("data", ParameterAttributes.None, assembly.MainModule.ImportReference(byteArrayType)));
            sendMessage.Parameters.Add(new ParameterDefinition("sendType", ParameterAttributes.None, assembly.MainModule.ImportReference(sendType)));

            var il = sendMessage.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(originalSendMessage));
            il.Emit(OpCodes.Ret);

            sendMessage.Body.MaxStackSize = 3;
            connection.Methods.Add(sendMessage);
        }
    }

    private static void PatchSteamUser(AssemblyDefinition assembly)
    {
        var steamClient = assembly.MainModule.GetType("Steamworks.SteamClient");
        var steamUser = assembly.MainModule.GetType("Steamworks.SteamUser");

        var steamIdProperty = steamClient.Properties.First(p => p.Name == "SteamId");
        var getSteamId = steamIdProperty.GetMethod;

        var originalGetAuthSessionTicket = steamUser.Methods.First(m => m.Name == "GetAuthSessionTicket" && m.Parameters.Count == 1);

        if (originalGetAuthSessionTicket != null)
        {
            Logger.LogInfo("Patching SteamUser.GetAuthSessionTicket method");

            var getAuthSessionTicket = new MethodDefinition(
                "GetAuthSessionTicket",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Static,
                assembly.MainModule.GetType("Steamworks.AuthTicket")
            );

            var il = getAuthSessionTicket.Body.GetILProcessor();
            il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(getSteamId));
            il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(originalGetAuthSessionTicket));
            il.Emit(OpCodes.Ret);

            steamUser.Methods.Add(getAuthSessionTicket);
        }
    }

    private static void PatchConnectionManager(AssemblyDefinition assembly)
    {
        var connectionManager = assembly.MainModule.GetType("Steamworks.ConnectionManager");

        var originalReceive = connectionManager.Methods.First(m => m.Name == "Receive" && m.Parameters.Count == 2);

        if (originalReceive != null)
        {
            Logger.LogInfo("Patching ConnectionManager.Receive method");

            var receive = new MethodDefinition(
                "Receive",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                assembly.MainModule.TypeSystem.Void
            );

            receive.Parameters.Add(new ParameterDefinition("bufferSize", ParameterAttributes.None, assembly.MainModule.TypeSystem.Int32));

            var il = receive.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(originalReceive));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);

            connectionManager.Methods.Add(receive);
        }

        var originalClose = connectionManager.Methods.First(m =>
            m.Name == "Close" &&
            m.Parameters.Count == 3 &&
            m.Parameters[1].ParameterType.FullName == "System.Int32" &&
            m.Parameters[2].ParameterType.FullName == "System.String"
        );

        if (originalClose != null)
        {
            Logger.LogInfo("Patching ConnectionManager.Close method");

            var close = new MethodDefinition(
                "Close",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                assembly.MainModule.TypeSystem.Void
            );

            var il = close.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ldstr, "Closing connection");
            il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(originalClose));
            il.Emit(OpCodes.Ret);

            close.Body.MaxStackSize = 4;
            connectionManager.Methods.Add(close);
        }
    }

    private static void PatchSocketManager(AssemblyDefinition assembly)
    {
        var socketManager = assembly.MainModule.GetType("Steamworks.SocketManager");

        var originalReceive = socketManager.Methods.First(m => m.Name == "Receive" && m.Parameters.Count == 2);

        if (originalReceive != null)
        {
            Logger.LogInfo("Patching SocketManager.Receive method");

            var receive = new MethodDefinition(
                "Receive",
                MethodAttributes.Public | MethodAttributes.HideBySig,
                assembly.MainModule.TypeSystem.Void
            );

            receive.Parameters.Add(new ParameterDefinition("bufferSize", ParameterAttributes.None, assembly.MainModule.TypeSystem.Int32));

            var il = receive.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Call, assembly.MainModule.ImportReference(originalReceive));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ret);

            socketManager.Methods.Add(receive);
        }
    }

    public static void Finish()
    {
        _patched?.Dispose();
    }
}
