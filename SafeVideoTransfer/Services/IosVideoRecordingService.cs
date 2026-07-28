using AVFoundation;
using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace SafeVideoTransfer.Services;

public sealed class IosVideoRecordingService : IVideoRecordingService
{
	private static readonly object DelegateGate = new();
	private static readonly Dictionary<nint, CameraPickerDelegate> ActiveDelegates = [];

	public async Task<Models.VideoRecordingResult?> RecordAsync(
		string destinationPath, CancellationToken cancellationToken)
	{
		var camera = await Permissions.RequestAsync<Permissions.Camera>();
		var microphone = await Permissions.RequestAsync<Permissions.Microphone>();
		if (camera != PermissionStatus.Granted || microphone != PermissionStatus.Granted)
			throw new UnauthorizedAccessException("Camera and microphone permission are required.");
		if (!UIImagePickerController.IsSourceTypeAvailable(UIImagePickerControllerSourceType.Camera))
			throw new NotSupportedException("No camera is available. Use a physical iPhone for recording.");

		var started = DateTimeOffset.UtcNow;
		var temporaryUrl = await PresentCameraAsync(cancellationToken);
		if (temporaryUrl is null) return null;

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
			File.Move(temporaryUrl.Path!, destinationPath, true);
			var asset = AVAsset.FromUrl(NSUrl.FromFilename(destinationPath));
			var seconds = asset.Duration.Seconds;
			return new Models.VideoRecordingResult(
				double.IsFinite(seconds) ? TimeSpan.FromSeconds(seconds) : DateTimeOffset.UtcNow - started);
		}
		finally
		{
			if (File.Exists(temporaryUrl.Path)) File.Delete(temporaryUrl.Path);
		}
	}

	private static Task<NSUrl?> PresentCameraAsync(CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<NSUrl?>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		MainThread.BeginInvokeOnMainThread(() =>
		{
			var picker = new UIImagePickerController
			{
				SourceType = UIImagePickerControllerSourceType.Camera,
				MediaTypes = [UTTypes.Movie.Identifier],
				CameraCaptureMode = UIImagePickerControllerCameraCaptureMode.Video,
				VideoQuality = UIImagePickerControllerQualityType.High
			};
			var pickerDelegate = new CameraPickerDelegate(completion);
			picker.Delegate = pickerDelegate;
			lock (DelegateGate) ActiveDelegates[picker.Handle] = pickerDelegate;
			picker.ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
			TopViewController()?.PresentViewController(picker, true, null);
		});
		cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
		return completion.Task;
	}

	private static UIViewController? TopViewController()
	{
		var controller = UIApplication.SharedApplication.ConnectedScenes
			.OfType<UIWindowScene>().SelectMany(x => x.Windows)
			.FirstOrDefault(x => x.IsKeyWindow)?.RootViewController;
		while (controller?.PresentedViewController is not null)
			controller = controller.PresentedViewController;
		return controller;
	}

	private sealed class CameraPickerDelegate(TaskCompletionSource<NSUrl?> completion)
		: UIImagePickerControllerDelegate
	{
		public override void FinishedPickingMedia(
			UIImagePickerController picker, NSDictionary info)
		{
			var url = info[UIImagePickerController.MediaURL] as NSUrl;
			picker.DismissViewController(true, () =>
			{
				ReleaseDelegate(picker);
				completion.TrySetResult(url);
			});
		}

		public override void Canceled(UIImagePickerController picker) =>
			picker.DismissViewController(true, () =>
			{
				ReleaseDelegate(picker);
				completion.TrySetResult(null);
			});
	}

	private static void ReleaseDelegate(UIImagePickerController picker)
	{
		lock (DelegateGate) ActiveDelegates.Remove(picker.Handle);
	}
}
