// Meeting Assistant — saves Windows Live Captions as a transcript + a local chat model, all on-device.
//
// Based on Microsoft's "11-foundrylocal-live-transcription" sample (MIT license, see LICENSE).
// Files:
//   Program.cs      – starts the app
//   MainForm.cs     – the window (buttons, transcript, chat)
//   CaptionReader.cs – reads Windows Live Captions and saves each finished sentence
//   CaptionText.cs   – works out which caption sentences are new and finished
//   Assistant.cs    – phi-4-mini chat, summaries and training notes
//   Foundry.cs      – one-time Foundry Local setup for the chat model

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
