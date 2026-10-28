using Canora.ViewModels;
using MediaPlayer;

namespace Canora;

// Successivo e precedente dai tasti multimediali, dal Centro di controllo e dalle cuffie, come i pulsanti della taskbar su
// Windows. MediaElement registra solo play, pausa e seek: la coda vive in PlayerViewModel, non nel player nativo.
static class RemoteCommands
{
	public static void Attach(PlayerViewModel player)
	{
		var center = MPRemoteCommandCenter.Shared;
		center.NextTrackCommand.Enabled = true;
		center.NextTrackCommand.AddTarget(_ => Run(() => player.NextCommand.Execute(null)));
		center.PreviousTrackCommand.Enabled = true;
		center.PreviousTrackCommand.AddTarget(_ => Run(() => player.PreviousCommand.Execute(null)));
	}

	static MPRemoteCommandHandlerStatus Run(Action command)
	{
		MainThread.BeginInvokeOnMainThread(command);
		return MPRemoteCommandHandlerStatus.Success;
	}
}
