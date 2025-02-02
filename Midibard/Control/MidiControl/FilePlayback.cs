using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Logging;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Standards;
using Melanchall.DryWetMidi.Tools;
using MidiBard.Control.CharacterControl;
using MidiBard.Control.MidiControl.PlaybackInstance;
using MidiBard.Managers.Ipc;
using MidiBard.Util;
using static MidiBard.MidiBard;
using System.IO;
using MidiBard.Common;
using MidiBard.Managers;
using MidiBard.HSC;

namespace MidiBard.Control.MidiControl;

public static class FilePlayback
{
    private static readonly Regex regex = new Regex(@"^#.*?([-|+][0-9]+).*?#", RegexOptions.Compiled | RegexOptions.IgnoreCase);



    public static (BardPlayback playback, TimedEventWithTrackChunkIndex[] timedEvs) GetPlayback(TimedEventWithTrackChunkIndex[] timedEvs, TempoMap tempoMap, string trackName)
    {
        PluginLog.Information($"[LoadPlayback] -> {trackName} START");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            MidiBard.CurrentTMap = tempoMap;
        }
        catch (Exception e)
        {
            PluginLog.Warning("[LoadPlayback] error when getting file TempoMap, using default TempoMap instead.");
            MidiBard.CurrentTMap = TempoMap.Default;
        }
        PluginLog.Information($"[LoadPlayback] -> {trackName} 1 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        try
        {
            MidiBard.CurrentTracks =

                timedEvs.GroupBy(to => (int)to.Metadata)
                    .Select(to => new { index = to.Key, track = TimedObjectUtilities.ToTrackChunk(to.ToArray()) })
                     .Where(c => c.track.GetNotes().Any())
                    .OrderBy(t => t.index)
                .Select((t, index) =>
                {
                    var notes = t.track.GetNotes().ToArray();
                    return (t.track, GetTrackInfos(notes, t.track, index));
                }).ToList();
        }
        catch (Exception exception1)
        {
            PluginLog.Warning(exception1, $"[LoadPlayback] error when parsing tracks, falling back to generated NoteEvent playback.");

            try
            {

                MidiBard.CurrentTracks =
                timedEvs.GroupBy(to => (int)to.Metadata)
                    .Select(to => new { index = to.Key, track = TimedObjectUtilities.ToTrackChunk(to.ToArray()) })
                     .Where(c => c.track.GetNotes().Any())
                    .OrderBy(t => t.index)
                .Select((t, index) =>
                {
                    var noteEvents = t.track.Events.Where(i => i is NoteEvent or ProgramChangeEvent or TextEvent);
                    var notes = noteEvents.GetNotes().ToArray();
                    var trackChunk = new TrackChunk(noteEvents);
                    return (trackChunk, GetTrackInfos(notes, trackChunk, index));
                }).ToList();
            }
            catch (Exception exception2)
            {
                PluginLog.Error(exception2, "[LoadPlayback] still errors? check your file");
                throw;
            }
        }
        PluginLog.Information($"[LoadPlayback] -> {trackName} 2 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        //int givenIndex = 0;
        //CurrentTracks.ForEach(tuple => tuple.trackInfo.Index = givenIndex++);

        var timedEvents = MidiBard.CurrentTracks.Select(i => i.trackChunk).AsParallel()
            .SelectMany((chunk, index) => chunk.GetTimedEvents().Select(e =>
            {
                var compareValue = e.Event switch
                {
                        //order chords so they always play from low to high
                    NoteOnEvent noteOn => noteOn.NoteNumber,
                        //order program change events so they always get processed before notes 
                    ProgramChangeEvent => -2,
                        //keep other unimportant events order
                    _ => -1
                };
                return (compareValue, timedEvent: new TimedEventWithTrackChunkIndex(e.Event, e.Time, index));
            }))
            .OrderBy(e => e.timedEvent.Time)
            .ThenBy(i => i.compareValue)
            .Select(i => i.timedEvent).ToArray(); //this is crucial as have executed a parallel query 

        PluginLog.Information($"[LoadPlayback] -> {trackName} 3 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        //var (programTrackChunk, programTrackInfo) =
        //    CurrentTracks.FirstOrDefault(i => Regex.IsMatch(i.trackInfo.TrackName, @"^Program:.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase));

        Array.Fill(MidiBard.CurrentOutputDevice.Channels, new BardPlayDevice.ChannelState());

        PluginLog.Information($"[LoadPlayback] -> {trackName} 3.1 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        var playback = new BardPlayback(timedEvents, MidiBard.CurrentTMap, new MidiClockSettings { CreateTickGeneratorCallback = () => new HighPrecisionTickGenerator() })
        {
            InterruptNotesOnStop = true,
            Speed = Configuration.config.playSpeed,
            TrackProgram = true
        };

        PluginLog.Information($"[LoadPlayback] -> {trackName} 4 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        PluginLog.Information($"[LoadPlayback] Channels for {trackName}:");
        for (int i = 0; i < MidiBard.CurrentOutputDevice.Channels.Length; i++)
        {
            uint prog = MidiBard.CurrentOutputDevice.Channels[i].Program;
            PluginLog.Information($"  - [{i}]: {Util.ProgramNames.GetGMProgramName((byte)prog)} ({prog})");
        }

        playback.Finished += HSCM_Playback_Finished;
        PluginLog.Information($"[LoadPlayback] -> {trackName} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");

        return (playback, timedEvs);
    }


    public static BardPlayback GetFilePlayback(MidiFile midifile, string trackName)
    {
        PluginLog.Information($"[LoadPlayback] -> {trackName} START");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            CurrentTMap = midifile.GetTempoMap();
        }
        catch (Exception e)
        {
            PluginLog.Warning("[LoadPlayback] error when getting file TempoMap, using default TempoMap instead.");
            CurrentTMap = TempoMap.Default;
        }
        PluginLog.Information($"[LoadPlayback] -> {trackName} 1 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        try
        {
            CurrentTracks = midifile.GetTrackChunks()
                .Select((t, i) => new { track = t, index = i})
                .Where(t => t.track.Events.Any(ev => ev is NoteOnEvent))
                .OrderBy(t => t.index)
                .Select((t, index) =>
                {
                    var notes = t.track.GetNotes().ToArray();
                    return (t.track, GetTrackInfos(notes, t.track, index));
                }).ToList();
        }
        catch (Exception exception1)
        {
            PluginLog.Warning(exception1, $"[LoadPlayback] error when parsing tracks, falling back to generated NoteEvent playback.");

            try
            {
                PluginLog.Debug($"[LoadPlayback] file.Chunks.Count {midifile.Chunks.Count}");
                var trackChunks = midifile.GetTrackChunks().ToList();
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.Count {trackChunks.Count}");
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.First {trackChunks.First()}");
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.Events.Count {trackChunks.First().Events.Count}");
                PluginLog.Debug($"[LoadPlayback] file.GetTrackChunks.Events.OfType<NoteEvent>.Count {trackChunks.First().Events.OfType<NoteEvent>().Count()}");


                CurrentTracks = midifile.GetTrackChunks()
                    .Select((t, i) => new { track = t, index = i })
                    .Where(t => t.track.Events.Any(ev => ev is NoteOnEvent))
                    .OrderBy(t => t.index)
                    .Select((t, index) =>
                    {
                        var noteEvents = t.track.Events.Where(i => i is NoteEvent or ProgramChangeEvent or TextEvent);
                        var notes = noteEvents.GetNotes().ToArray();
                        var trackChunk = new TrackChunk(noteEvents);
                        return (trackChunk, GetTrackInfos(notes, trackChunk, index));
                    }).ToList();
            }
            catch (Exception exception2)
            {
                PluginLog.Error(exception2, "[LoadPlayback] still errors? check your file");
                throw;
            }
        }
        PluginLog.Information($"[LoadPlayback] -> {trackName} 2 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        //int givenIndex = 0;
        //CurrentTracks.ForEach(tuple => tuple.trackInfo.Index = givenIndex++);

        var timedEvents = CurrentTracks.Select(i => i.trackChunk).AsParallel()
            .SelectMany((chunk, index) => chunk.GetTimedEvents().Select(e =>
            {
                var compareValue = e.Event switch
                {
                    //order chords so they always play from low to high
                    NoteOnEvent noteOn => noteOn.NoteNumber,
                    //order program change events so they always get processed before notes 
                    ProgramChangeEvent => -2,
                    //keep other unimportant events order
                    _ => -1
                };
                return (compareValue, timedEvent: new TimedEventWithTrackChunkIndex(e.Event, e.Time, index));
            }))
            .OrderBy(e => e.timedEvent.Time)
            .ThenBy(i => i.compareValue)
            .Select(i => i.timedEvent).ToArray(); //this is crucial as have executed a parallel query 

        PluginLog.Information($"[LoadPlayback] -> {trackName} 3 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        //var (programTrackChunk, programTrackInfo) =
        //    CurrentTracks.FirstOrDefault(i => Regex.IsMatch(i.trackInfo.TrackName, @"^Program:.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase));

        Array.Fill(CurrentOutputDevice.Channels, new BardPlayDevice.ChannelState());
        //if (programTrackChunk is not null && programTrackInfo is not null)
        //{
        //	PluginLog.Verbose($"FOUND PROGRAM TRACK i:{programTrackInfo.Index}");

        //	foreach (ProgramChangeEvent programChangeEvent in timedEvents
        //		.Where(e => (int)e.Metadata == programTrackInfo.Index && e.Time == 0)
        //		.Select(e => e.Event)
        //		.OfType<ProgramChangeEvent>())
        //	{
        //		FourBitNumber channel = programChangeEvent.Channel;
        //		SevenBitNumber prog = (SevenBitNumber)Math.Max(0, programChangeEvent.ProgramNumber + 1);
        //		//PluginLog.Verbose($"FOUND INIT PROGRAMCHANGE c:{channel} p:{prog}");

        //		for (int i = 0; i < CurrentOutputDevice.Channels.Length; i++)
        //		{
        //			CurrentOutputDevice.Channels[i].Program = prog;
        //		}
        //	}
        //}
        //else
        //{
        //	SevenBitNumber prog = InstrumentPrograms[CurrentInstrument].id;
        //	for (int i = 0; i < CurrentOutputDevice.Channels.Length; i++)
        //	{
        //		CurrentOutputDevice.Channels[i].Program = prog;
        //	}
        //}
        PluginLog.Information($"[LoadPlayback] -> {trackName} 3.1 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        var playback = new BardPlayback(timedEvents, CurrentTMap, new MidiClockSettings { CreateTickGeneratorCallback = () => new HighPrecisionTickGenerator() })
        {
            InterruptNotesOnStop = true,
            Speed = Configuration.config.playSpeed,
            TrackProgram = true,
#if DEBUG
            NoteCallback = (data, time, length, playbackTime) =>
            {
                PluginLog.Verbose($"[NOTE] {new Note(data.NoteNumber)} time:{time} len:{length} time:{playbackTime}");
                return data;
            }
#endif
        };
        PluginLog.Information($"[LoadPlayback] -> {trackName} 4 in {stopwatch.Elapsed.TotalMilliseconds} ms");
        PluginLog.Information($"[LoadPlayback] Channels for {trackName}:");
        for (int i = 0; i < CurrentOutputDevice.Channels.Length; i++)
        {
            uint prog = CurrentOutputDevice.Channels[i].Program;
            PluginLog.Information($"  - [{i}]: {ProgramNames.GetGMProgramName((byte)prog)} ({prog})");
        }

        playback.Finished += Configuration.config.useHscmOverride ? HSCM_Playback_Finished : MidiBard_Playback_Finished;
        PluginLog.Information($"[LoadPlayback] -> {trackName} OK! in {stopwatch.Elapsed.TotalMilliseconds} ms");

        return playback;
    }

    private static TrackInfo GetTrackInfos(Note[] notes, TrackChunk i, int index)
    {
        var eventsCollection = i.Events;
        var TrackNameEventsText = eventsCollection.OfType<SequenceTrackNameEvent>().Select(j => j.Text.Replace("\0", string.Empty).Trim()).Distinct().ToArray();
        var TrackName = TrackNameEventsText.FirstOrDefault() ?? "Untitled";
        var IsProgramControlled = Regex.IsMatch(TrackName, @"^Program:.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var timedNoteOffEvent = notes.LastOrDefault()?.GetTimedNoteOffEvent();
        return new TrackInfo
        {
            //TextEventsText = eventsCollection.OfType<TextEvent>().Select(j => j.Text.Replace("\0", string.Empty).Trim()).Distinct().ToArray(),
            ProgramChangeEventsText = eventsCollection.OfType<ProgramChangeEvent>().Select(j => $"channel {j.Channel}, {j.GetGMProgramName()}").Distinct().ToArray(),
            TrackNameEventsText = TrackNameEventsText,
            HighestNote = notes.MaxElement(j => (int)j.NoteNumber),
            LowestNote = notes.MinElement(j => (int)j.NoteNumber),
            NoteCount = notes.Length,
            DurationMetric = timedNoteOffEvent?.TimeAs<MetricTimeSpan>(CurrentTMap) ?? new MetricTimeSpan(),
            DurationMidi = timedNoteOffEvent?.Time ?? 0,
            TrackName = TrackName,
            IsProgramControlled = IsProgramControlled,
            Index = index
        };
    }

    public static DateTime? waitUntil { get; set; } = null;
    public static DateTime? waitStart { get; set; } = null;
    public static bool isWaiting => waitUntil != null && DateTime.Now < waitUntil;

    public static float waitProgress
    {
        get
        {
            float valueTotalMilliseconds = 1;
            if (isWaiting)
            {
                try
                {
                    if (waitUntil != null)
                        if (waitStart != null)
                            valueTotalMilliseconds = 1 -
                                                     (float)((waitUntil - DateTime.Now).Value.TotalMilliseconds /
                                                             (waitUntil - waitStart).Value.TotalMilliseconds);
                }
                catch (Exception e)
                {
                    PluginLog.Error(e, "error when get current wait progress");
                }
            }

            return valueTotalMilliseconds;
        }
    }

    private static void MidiBard_Playback_Finished(object sender, EventArgs e)
    {
        Task.Run(async () =>
        {
            try
            {
                if (MidiBard.AgentMetronome.EnsembleModeRunning)
                    return;
                if (!PlaylistManager.FilePathList.Any())
                    return;

                PerformWaiting(Configuration.config.secondsBetweenTracks);
                if (needToCancel)
                {
                    needToCancel = false;
                    return;
                }

                switch ((PlayMode)Configuration.config.PlayMode)
                {
                    case PlayMode.Single:
                        break;

                    case PlayMode.SingleRepeat:
                        CurrentPlayback.MoveToStart();
                        MidiPlayerControl.DoPlay();
                        break;

                    case PlayMode.ListOrdered:
                        if (PlaylistManager.CurrentPlaying + 1 < PlaylistManager.FilePathList.Count)
                        {
                            if (await LoadPlayback(PlaylistManager.CurrentPlaying + 1, true))
                            {
                            }
                        }

                        break;

                    case PlayMode.ListRepeat:
                        if (PlaylistManager.CurrentPlaying + 1 < PlaylistManager.FilePathList.Count)
                        {
                            if (await LoadPlayback(PlaylistManager.CurrentPlaying + 1, true))
                            {
                            }
                        }
                        else
                        {
                            if (await LoadPlayback(0, true))
                            {
                            }
                        }

                        break;

                    case PlayMode.Random:

                        if (PlaylistManager.FilePathList.Count == 1)
                        {
                            CurrentPlayback.MoveToStart();
                            break;
                        }

                        try
                        {
                            var r = new Random();
                            int nexttrack;
                            do
                            {
                                nexttrack = r.Next(0, PlaylistManager.FilePathList.Count);
                            } while (nexttrack == PlaylistManager.CurrentPlaying);

                            if (await LoadPlayback(nexttrack, true))
                            {
                            }
                        }
                        catch (Exception exception)
                        {
                            PluginLog.Error(exception, "error when random next");
                        }

                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, "Unexpected exception when Playback finished.");
            }
        });
    }



    private static void HSCM_Playback_Finished(object sender, EventArgs e)
    {
        Task.Run(async() =>
        {
            try
            {
                if (MidiBard.AgentMetronome.EnsembleModeRunning)
                {
                    if (Configuration.config.useHscmCloseOnFinish)
                    {
                        PerformHelpers.WaitUntilChanged(() => !MidiBard.AgentMetronome.EnsembleModeRunning, 100, 5000);
                        HSC.PerformHelpers.ClosePerformance();
                    }
                }
                else
                {
                    if (Configuration.config.useHscmCloseOnFinish)
                        HSC.PerformHelpers.ClosePerformance();
                }

                FilePlayback.PerformWaiting(Configuration.config.secondsBetweenTracks);
                if (needToCancel)
                {
                    needToCancel = false;
                    return;
                }

                switch ((PlayMode)Configuration.config.PlayMode)
                {
                    case PlayMode.Single:
                        break;

                    case PlayMode.SingleRepeat:
                        CurrentPlayback.MoveToStart();
                        Control.MidiControl.MidiPlayerControl.DoPlay();
                        break;

                    case PlayMode.ListOrdered:
                        if (Managers.PlaylistManager.CurrentPlaying + 1 < Managers.PlaylistManager.FilePathList.Count)
                        {
                            if (await LoadPlayback(Managers.PlaylistManager.CurrentPlaying + 1, true))
                            {
                            }
                        }

                        break;

                    case PlayMode.ListRepeat:
                        if (Managers.PlaylistManager.CurrentPlaying + 1 < Managers.PlaylistManager.FilePathList.Count)
                        {
                            if (await LoadPlayback(Managers.PlaylistManager.CurrentPlaying + 1, true))
                            {
                            }
                        }
                        else
                        {
                            if (await LoadPlayback(0, true))
                            {
                            }
                        }

                        break;

                    case PlayMode.Random:

                        if (Managers.PlaylistManager.FilePathList.Count == 1)
                        {
                            CurrentPlayback.MoveToStart();
                            break;
                        }

                        try
                        {
                            var r = new Random();
                            int nexttrack;
                            do
                            {
                                nexttrack = r.Next(0, Managers.PlaylistManager.FilePathList.Count);
                            } while (nexttrack == Managers.PlaylistManager.CurrentPlaying);

                            if (await LoadPlayback(nexttrack, true))
                            {
                            }
                        }
                        catch (Exception exception)
                        {
                            PluginLog.Error(exception, "error when random next");
                        }

                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception exception)
            {
                PluginLog.Error(exception, "Unexpected exception when Playback finished.");
            }
        });
    }

    /// <summary>
    /// for now just assigns ensemble member to tracks from hsc playlist before playback for current MIDI
    /// </summary>

    internal static async Task<bool> LoadPlayback(int index, bool startPlaying = false, bool switchInstrument = true)
    {
        if (Configuration.config.useHscmOverride)
            return await HSCM_LoadPlayback(index, startPlaying);
        return await MidiBard_LoadPlayback(index, startPlaying, switchInstrument);
    }

    //cached playback - dont load midi file if already stored for huge performance improvements doing adjustments e.g chord trimming
    //this should be included in MidiBard_LoadPlayback in the future as its very beneficial 
    private static async Task<bool> HSCM_LoadPlayback(int index, bool startPlaying = false, bool switchInstrument = true)
    {
        var wasPlaying = IsPlaying;
        CurrentPlayback?.Dispose();
        CurrentPlayback = null;

        string songName = Managers.PlaylistManager.FilePathList[index].displayName;

        if (!Configuration.config.useHscmSongCache)//not using cache - always load midi file
        {
            var midiFile = await PlaylistManager.LoadMidiFile(index);

            if (midiFile == null)
            {
                // delete file if can't be loaded(likely to be deleted locally)
                PluginLog.Debug($"[LoadPlayback] removing {index}");
                //PluginLog.Debug($"[LoadPlayback] removing {PlaylistManager.FilePathList[index].path}");
                Managers.PlaylistManager.FilePathList.RemoveAt(index);
                return false;
            }

            CurrentPlayback = await Task.Run(() => HSC.PlaybackUtilities.GetProcessedMidiPlayback(midiFile, songName)); 
        }
        else// using cache
        {
            if (!HSC.SongCache.IsCached(songName))//song not cached? load midi file and store events
            {

                PluginLog.Information($"Song '{songName}' not in HSCM cache. adding");

                var midiFile = await PlaylistManager.LoadMidiFile(index);
                var tempoMap = midiFile.GetTempoMap();

                if (midiFile == null)
                {
                    // delete file if can't be loaded(likely to be deleted locally)
                    PluginLog.Debug($"[LoadPlayback] removing {index}");
                    //PluginLog.Debug($"[LoadPlayback] removing {PlaylistManager.FilePathList[index].path}");
                    Managers.PlaylistManager.FilePathList.RemoveAt(index);
                    return false;
                } 

                //fetch timed events 
                var timedEvs = await Task.Run(() => HSC.PlaybackUtilities.GetTimedEvents(midiFile));

                //store in cache
                SongCache.AddOrUpdate(songName, (tempoMap, timedEvs));

                CurrentPlayback = await Task.Run(() => HSC.PlaybackUtilities.GetProcessedPlayback(timedEvs, tempoMap, songName));
            }//song cached? fetch from cache and prepare playback
            else
            {
                CurrentPlayback = await Task.Run(() => HSC.PlaybackUtilities.GetCachedPlayback(songName));
            }
        }

        Ui.RefreshPlotData();
        Managers.PlaylistManager.CurrentPlaying = index;

        //HSCM.PlaylistManager.ApplySettings(true);//this should allow the HSCM playlist to be looped 

        if (switchInstrument)
        {
            try
            {
                await PerformHelpers.SwitchInstrumentFromSong();
            }
            catch (Exception e)
            {
                PluginLog.Warning(e.ToString());
            }
        }

        PrepareLyrics(index);//dont forget this!

        if (DalamudApi.api.PartyList.IsInParty() && Configuration.config.useHscmSendReadyCheck)//dont start the song if we are sending a ready check
            return true;

        if (MidiBard.CurrentInstrument != 0 && (wasPlaying || startPlaying))
            Control.MidiControl.MidiPlayerControl.DoPlay();

        if (wasPlaying && HSC.Settings.PrevTime != null)//jump back to the position before the song restarted e.g after chord trimming if we should
            CurrentPlayback.MoveToTime(HSC.Settings.PrevTime);


        return true;
    }

    private static async Task<bool> MidiBard_LoadPlayback(int index, bool startPlaying = false, bool switchInstrument = true)
    {
        var wasPlaying = IsPlaying;
        CurrentPlayback?.Dispose();
        CurrentPlayback = null;
        MidiFile midiFile = await PlaylistManager.LoadMidiFile(index);
        if (midiFile == null)
        {
            // delete file if can't be loaded(likely to be deleted locally)
            PluginLog.Debug($"[LoadPlayback] removing {index}");
            //PluginLog.Debug($"[LoadPlayback] removing {PlaylistManager.FilePathList[index].path}");
            PlaylistManager.FilePathList.RemoveAt(index);
            return false;
        }
        else
        {
            CurrentPlayback = await Task.Run(() => GetFilePlayback(midiFile, PlaylistManager.FilePathList[index].displayName));
            Ui.RefreshPlotData();
            PlaylistManager.CurrentPlaying = index;
            DalamudApi.api.ChatGui.Print(String.Format("[MidiBard 2] Now Playing: {0}", PlaylistManager.FilePathList[index].fileName));

            var songName = PlaylistManager.FilePathList[index].fileName;
            if (switchInstrument)
            {
                try
                {
                    await SwitchInstrument.WaitSwitchInstrumentForSong(songName);
                }
                catch (Exception e)
                {
                    PluginLog.Warning(e.ToString());
                }
            }


            if (switchInstrument && (wasPlaying || startPlaying))
            {
                MidiPlayerControl.DoPlay();
            }

            PrepareLyrics(index);

            return true;
        }
    }

    private static void PrepareLyrics(int index)
    {


        string[] pathArray = PlaylistManager.FilePathList[index].path.Split("\\");
        string LrcPath = "";
        string fileName = Path.GetFileNameWithoutExtension(PlaylistManager.FilePathList[index].path) + ".lrc";
        for (int i = 0; i < pathArray.Length - 1; i++)
        {
            LrcPath += pathArray[i];
            LrcPath += "\\";
        }

        LrcPath += fileName;
        Lrc lrc = Lrc.InitLrc(LrcPath);
        MidiPlayerControl.LrcTimeStamps = Lrc._lrc.LrcWord.Keys.ToList();
#if DEBUG
            PluginLog.LogVerbose($"Title: {lrc.Title}, Artist: {lrc.Artist}, Album: {lrc.Album}, LrcBy: {lrc.LrcBy}, Offset: {lrc.Offset}");
            foreach (var pair in lrc.LrcWord)
            {
                PluginLog.LogVerbose($"{pair.Key}, {pair.Value}");
            }

#endif
    }

    private static bool needToCancel { get; set; } = false;

    internal static void PerformWaiting(float seconds)
    {
        waitStart = DateTime.Now;
        waitUntil = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < waitUntil)
        {
            Thread.Sleep(10);
        }

        waitStart = null;
        waitUntil = null;
    }

    internal static void CancelWaiting()
    {
        waitStart = null;
        waitUntil = null;
        needToCancel = true;
    }

    internal static void StopWaiting()
    {
        waitStart = null;
        waitUntil = null;
    }
}