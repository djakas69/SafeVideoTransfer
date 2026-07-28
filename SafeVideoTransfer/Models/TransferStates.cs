namespace SafeVideoTransfer.Models;

public enum RecordingState { None, Recording, Recorded, Failed }
public enum UploadState { NotStarted, Uploading, Interrupted, Failed, Uploaded }
public enum VerificationState { NotStarted, Verifying, Failed, Verified }
public enum DeletionState { NotRequested, Kept, Deleted, Failed }
