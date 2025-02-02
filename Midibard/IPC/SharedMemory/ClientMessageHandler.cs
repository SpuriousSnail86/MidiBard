﻿using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using MidiBard.HSC;
using MidiBard.IPC.SharedMemory;
using SharedMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace MidiBard
{
    public partial class MidiBard
    {

        private static void StopClientMessageHandler()
        {
            hscmConnected = false;

            waitHandle.Set();
            waitHandle?.Close();
            hscmWaitHandle?.Set();
            hscmWaitHandle?.Close();

            msgHandler.ChangeSongMessageReceived -= MsgHandler_ChangeSongMessageReceived;
            msgHandler.ReloadPlaylistMessageReceived -= MsgHandler_ReloadPlaylistMessageReceived;
            msgHandler.ReloadPlaylistSettingsMessageReceived -= MsgHandler_ReloadPlaylistSettingsMessageReceived;
            msgHandler.SwitchInstrumentsMessageReceived -= MsgHandler_SwitchInstrumentsMessageReceived;
            msgHandler.RestartHscmOverrideMessageReceived -= MsgHandler_RestartHscmOverrideMessageReceived;
            msgHandler.ClosePerformanceMessageReceived -= MsgHandler_ClosePerformanceMessageReceived;
            msgHandler.KillClientMessageReceived -= MsgHandler_KillClientMessageReceived; 
            msgHandler = null;
        }


        private static void StartClientMessageHander()
        {
            try
            {
                msgHandler = new IPC.SharedMemory.MessageHandler();

                msgHandler.ChangeSongMessageReceived += MsgHandler_ChangeSongMessageReceived;
                msgHandler.ReloadPlaylistMessageReceived += MsgHandler_ReloadPlaylistMessageReceived;
                msgHandler.ReloadPlaylistSettingsMessageReceived += MsgHandler_ReloadPlaylistSettingsMessageReceived;
                msgHandler.SwitchInstrumentsMessageReceived += MsgHandler_SwitchInstrumentsMessageReceived;
                msgHandler.RestartHscmOverrideMessageReceived += MsgHandler_RestartHscmOverrideMessageReceived;
                msgHandler.ClosePerformanceMessageReceived += MsgHandler_ClosePerformanceMessageReceived;
                msgHandler.KillClientMessageReceived += MsgHandler_KillClientMessageReceived;

                PluginLog.Information($"Started client message event handling.");

                while (hscmConnected && (DalamudApi.api.ClientState.IsLoggedIn || Configuration.config.hscmOfflineTesting))
                {
                    if (HSC.Settings.CharIndex == -1 || !Configuration.config.useHscmOverride) continue;//dont do anything until we have the char config loaded

                    if (!hscmConnected)
                    {
                        PluginLog.Information($"Stopping client message event handler.");
                        // Clean up here, then...
                        //cancelToken.ThrowIfCancellationRequested();
                        break;
                    }

                    PluginLog.Information($"Client waiting for message.");


                    bool success = waitHandle.WaitOne();

                    if (HSC.Settings.CharIndex == -1 || !Configuration.config.useHscmOverride) continue;//dont do anything until we have the char config loaded

                    if (!hscmConnected)
                    {
                        PluginLog.Information($"Stopping client message event handler.");
                        // Clean up here, then...
                        //cancelToken.ThrowIfCancellationRequested();
                        break;
                    }

                    PluginLog.Information($"Client message sent.");

                    if (success)
                        ConsumeMessage();
                    else
                    {
                        PluginLog.Error($"An error occured waiting on event signal");
                        break;
                    }

                    success = waitHandle.Reset();
                    if (!success)
                    {
                        PluginLog.Error($"An error occured when releasing event wait handle");
                        break;
                    }

                    Thread.Sleep(100);
                }

            }
            catch (Exception ex)
            {
                //ImGuiUtil.AddNotification(NotificationType.Error, $"Cannot connect to HSCM");
                PluginLog.Error($"An error occured when handling messages: {ex.Message}");
                //StopClientMessageHandler();
            }
        }

        private static void ConsumeMessage()
        {
            try
            {
                int[] buffer = new int[2];
                int total = Common.IPC.SharedMemory.Read(buffer, 2);
                PluginLog.Information($"Buffer: {buffer[0]} {buffer[1]}");
                if (total == 0)
                {
                    PluginLog.Error($"Could not read from shared memory");
                    return;
                }

                if (buffer[0] == 0)
                {
                    PluginLog.Information($"Shared memory buffer has been cleared");
                    return;
                }

                msgHandler?.HandleMessage((IPC.SharedMemory.MessageType)buffer[0], buffer[1]);
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Error processing shared memory buffer: {ex.Message}");
            }
        }

    }
}
