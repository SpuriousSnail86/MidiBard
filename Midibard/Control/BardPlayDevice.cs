using System;
using System.Linq;
using Dalamud.Logging;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Melanchall.DryWetMidi.Standards;
using MidiBard.Control.CharacterControl;
using MidiBard.HSC;
using MidiBard.Util;
using playlibnamespace;

namespace MidiBard.Control;

internal class BardPlayDevice : IOutputDevice
{
    internal struct ChannelState
    {
        public SevenBitNumber Program { get; set; }

        public ChannelState(SevenBitNumber? program)
        {
            this.Program = program ?? SevenBitNumber.MinValue;
        }
    }

    public readonly ChannelState[] Channels;
    public FourBitNumber CurrentChannel;

    public event EventHandler<MidiEventSentEventArgs> EventSent;

    public BardPlayDevice()
    {
        Channels = new ChannelState[16];
        CurrentChannel = FourBitNumber.MinValue;
    }

    public void PrepareForEventsSending()
    {
    }

    /// <summary>
    /// Midi events send from input device
    /// </summary>
    /// <param name="midiEvent">Raw midi event</param>
    public void SendEvent(MidiEvent midiEvent)
    {
        SendEventWithMetadata(midiEvent, null);
    }

    record MidiEventMetaData
    {
        public enum EventSource
        {
            Playback,
            MidiListener
        }

        public int TrackIndex { get; init; }
        public EventSource Source { get; init; }
    }

    /// <summary>
    /// Directly invoked by midi events sent from file playback
    /// </summary>
    /// <param name="midiEvent">Raw midi event</param>
    /// <param name="metadata">Currently is track index</param>
    /// <returns></returns>
    public virtual bool SendEventWithMetadata(MidiEvent midiEvent, object metadata)
    {

        if (!MidiBard.AgentPerformance.InPerformanceMode) return false;

        var trackIndex = (int?)metadata;
        if (trackIndex is { } trackIndexValue)
        {
            if (Configuration.config.SoloedTrack is { } soloing)
            {
                if (trackIndexValue != soloing)
                {
                    return false;
                }
            }
            else
            {
                if (!ConfigurationPrivate.config.EnabledTracks[trackIndexValue])
                {
                    return false;
                }
            }

            if (midiEvent is NoteOnEvent noteOnEvent)
            {
                if (MidiBard.PlayingGuitar)
                {
                    switch (Configuration.config.GuitarToneMode)
                    {
                        case GuitarToneMode.Off:
                            break;
                        case GuitarToneMode.Standard:
                            HandleToneSwitchEvent(noteOnEvent);
                            break;
                        case GuitarToneMode.Simple:
                            {
                                if (MidiBard.CurrentTracks[trackIndexValue].trackInfo.IsProgramControlled)
                                {
                                    HandleToneSwitchEvent(noteOnEvent);
                                }
                                break;
                            }
                        case GuitarToneMode.Override:
                            {
                                int tone = Configuration.config.TonesPerTrack[trackIndexValue];
                                playlib.GuitarSwitchTone(tone);

                                // PluginLog.Verbose($"[N][NoteOn][{trackIndex}:{noteOnEvent.Channel}] Overriding guitar tone {tone}");
                                break;
                            }
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        return SendMidiEvent(midiEvent, trackIndex);
    }

    protected void HandleToneSwitchEvent(NoteOnEvent noteOnEvent)
    {
        // if (CurrentChannel != noteOnEvent.Channel)
        // {
        //     PluginLog.Verbose($"[N][Channel][{trackIndex}:{noteOnEvent.Channel}] Changing channel from {CurrentChannel} to {noteOnEvent.Channel}");
        CurrentChannel = noteOnEvent.Channel;
        // }
        SevenBitNumber program = Channels[CurrentChannel].Program;
        if (MidiBard.ProgramInstruments.TryGetValue(program, out var instrumentId))
        {
            var instrument = MidiBard.Instruments[instrumentId];
            if (instrument.IsGuitar)
            {
                int tone = instrument.GuitarTone;
                playlib.GuitarSwitchTone(tone);
                // var (id, name) = MidiBard.InstrumentPrograms[MidiBard.ProgramInstruments[prog]];
                // PluginLog.Verbose($"[N][NoteOn][{trackIndex}:{noteOnEvent.Channel}] Changing guitar program to [{id} t:({tone})] {name} ({(GeneralMidiProgram)(byte)prog})");
            }
        }
    }

    protected unsafe virtual bool SendMidiEvent(MidiEvent midiEvent, int? trackIndex)
    {
        switch (midiEvent)
        {
            case ProgramChangeEvent @event:
                {
                    switch (Configuration.config.GuitarToneMode)
                    {
                        case GuitarToneMode.Off:
                            break;
                        case GuitarToneMode.Standard:
                            Channels[@event.Channel].Program = @event.ProgramNumber;
                            break;
                        case GuitarToneMode.Simple:
                            Array.Fill(Channels, new ChannelState(@event.ProgramNumber));
                            break;
                        case GuitarToneMode.Override:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    break;
                }
            case NoteOnEvent noteOnEvent:
                {
#if DEBUG
                    PluginLog.Verbose($"[NoteOnEvent] [{trackIndex}:{noteOnEvent.Channel}] {noteOnEvent.NoteNumber,-3}");
#endif
                    var noteNum = GetTranslatedNoteNum(noteOnEvent.NoteNumber, trackIndex, out int octave);
                    var s = $"[N][DOWN][{trackIndex}:{noteOnEvent.Channel}] {GetNoteName(noteOnEvent)} ({noteNum})";

                    if (noteNum is < 0 or > 36)
                    {
                        s += "(out of range)";
#if DEBUG
                        PluginLog.Verbose(s);
#endif
                        return false;
                    }

                    if (octave != 0) s += $"[adapted {octave:+#;-#;0} Oct]";

                    {
                        if (MidiBard.AgentPerformance.noteNumber - 39 == noteNum)
                        {
                            // release repeated note in order to press it again

                            if (playlib.ReleaseKey(noteNum))
                            {
                                MidiBard.AgentPerformance.Struct->PressingNoteNumber = -100;
#if DEBUG
                                PluginLog.Verbose($"[N][PUP ][{trackIndex}:{noteOnEvent.Channel}] {GetNoteName(noteOnEvent)} ({noteNum})");
#endif
                            }
                        }
#if DEBUG
                        PluginLog.Verbose(s);
#endif
                        if (playlib.PressKey(noteNum, ref MidiBard.AgentPerformance.Struct->NoteOffset,
                                ref MidiBard.AgentPerformance.Struct->OctaveOffset))
                        {
                            MidiBard.AgentPerformance.Struct->PressingNoteNumber = noteNum + 39;
                            return true;
                        }
                    }

                    break;
                }
            case NoteOffEvent noteOffEvent:
                {
                    var noteNum = GetTranslatedNoteNum(noteOffEvent.NoteNumber, trackIndex, out _);
                    if (noteNum is < 0 or > 36) return false;

                    if (MidiBard.AgentPerformance.Struct->PressingNoteNumber - 39 != noteNum)
                    {
#if DEBUG
                        PluginLog.Verbose($"[N][IGOR][{trackIndex}:{noteOffEvent.Channel}] {GetNoteName(noteOffEvent)} ({noteNum})");
#endif
                        return false;
                    }

                    // only release a key when it been pressing
#if DEBUG
                    PluginLog.Verbose($"[N][UP  ][{trackIndex}:{noteOffEvent.Channel}] {GetNoteName(noteOffEvent)} ({noteNum})");
#endif
                    if (playlib.ReleaseKey(noteNum))
                    {
                        MidiBard.AgentPerformance.Struct->PressingNoteNumber = -100;
                        return true;
                    }

                    break;
                }
        }

        return false;
    }

    protected static string GetNoteName(NoteEvent note) => $"{note.GetNoteName().ToString().Replace("Sharp", "#")}{note.GetNoteOctave()}";


    public static int GetTranslatedNote(int noteNumber, int? trackIndex, out int octave, bool plotting = false)
    {

        noteNumber = noteNumber - 48;

        octave = 0;

        noteNumber += Configuration.config.TransposeGlobal +
                     (Configuration.config.EnableTransposePerTrack && trackIndex is { } index ? Configuration.config.TransposePerTrack[index] : 0);

        if (Configuration.config.AdaptNotesOOR)
        {
            while (noteNumber < 0)
            {
                noteNumber += 12;
                octave++;
            }

            while (noteNumber > 36)
            {
                noteNumber -= 12;
                octave--;
            }
        }

        return noteNumber;
    }

    public static int GetTranslatedNoteNum(int noteNumber, int? trackIndex, out int octave, bool plotting = false)
    {

        noteNumber = noteNumber - 48;

        octave = 0;

        if (!Configuration.config.useHscmOverride)
            noteNumber += Configuration.config.TransposeGlobal +
                         (Configuration.config.EnableTransposePerTrack && trackIndex is { } index ? Configuration.config.TransposePerTrack[index] : 0);

        if (Configuration.config.AdaptNotesOOR)
        {
            if (noteNumber < 0)
            {
                noteNumber = (noteNumber + 1) % 12 + 11;
            }
            else if (noteNumber > 36)
            {
                noteNumber = (noteNumber - 1) % 12 + 25;
            }
        }

        return noteNumber;
    }
}