namespace NexusPipeline.Core.Records;

public enum RecordContentAvailability { Available, PartiallyRetired, Retired }

public sealed class RetiredContentException() : IOException("record_content_retired");
