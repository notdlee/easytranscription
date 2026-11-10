// Meeting Assistant — live meeting transcription + a local chat model, all on-device.
//
// Based on Microsoft's "11-foundrylocal-live-transcription" sample (MIT license, see LICENSE).
// Files:
//   Program.cs      – starts the app
//   MainForm.cs     – the window (buttons, transcript, chat)
//   Transcriber.cs  – mic + PC audio capture and live speech-to-text
//   Assistant.cs    – phi-4-mini chat, summaries and training notes
//   Foundry.cs      – one-time Foundry Local setup shared by both models

namespace MeetingAssistant;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
