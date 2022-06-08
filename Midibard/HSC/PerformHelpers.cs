﻿using Dalamud.Logging;
using MidiBard.HSC.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MidiBard.Common;
using Melanchall.DryWetMidi.Core;

namespace MidiBard.HSC
{
    public class PerformHelpers
    {
        public static bool ShouldPlayNote(MidiEvent ev, int trackIndex)
        {
            if (!(ev is NoteEvent))
                return true;

            var noteEv = ev as NoteEvent;

            //not a percussion note so play anyway
            if (!HSC.Settings.PercussionTracks.ContainsKey(trackIndex))
                return true;

            //percussion note - do percussion logic
            return HSC.Settings.PercussionNotes[trackIndex].ContainsKey((int)noteEv.NoteNumber) && HSC.Settings.PercussionNotes[trackIndex][(int)noteEv.NoteNumber];
        }

        public static uint GetInstrumentFromHscPlaylist(string fileName)
        {
            try
            {

                if (!HSC.Settings.PlaylistSettings.Settings.ContainsKey(fileName))
                    return 0;

                var songSettings = HSC.Settings.PlaylistSettings.Settings[fileName];

                if (songSettings.Tracks.IsNullOrEmpty())
                    return 1;

                var firstTrack = songSettings.Tracks.Values.FirstOrDefault(t => t.EnsembleMember == HSC.Settings.CharIndex);
                if (firstTrack == null)
                    return 0;

                uint insId = (uint)PerformanceHelpers.GetInstrumentFromName(firstTrack.EnsembleInstrument).Value;

                return insId;
            }

            catch (Exception e)
            {
                PluginLog.Error(e, $"Instrument switching from hsc playlist failed. {e.Message}");
                return 0;
            }
        }
    }
}
