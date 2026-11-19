using RomM.Saves;
using Xunit;

namespace RomM.Tests
{
    public class SaveSyncMessagesTests
    {
        // A sync runs at every launch and exit; one that moved nothing must not interrupt the player.
        [Fact]
        public void A_sync_that_moved_nothing_says_nothing()
        {
            Assert.Null(SaveSyncMessages.Describe(0, 0, 0, 0, null));
        }

        [Fact]
        public void A_download_and_an_upload_are_each_said()
        {
            Assert.Equal("Downloaded the newer save from RomM.", SaveSyncMessages.Describe(0, 1, 0, 0, null));
            Assert.Equal("Uploaded this save to RomM.", SaveSyncMessages.Describe(1, 0, 0, 0, null));
        }

        [Fact]
        public void A_resolved_conflict_says_which_side_won()
        {
            Assert.Contains("both sides had changed", SaveSyncMessages.Describe(0, 1, 1, 0, null));
            Assert.Contains("newer than RomM's", SaveSyncMessages.Describe(1, 0, 1, 0, null));
        }

        // A failed automatic sync is otherwise only in the log.
        [Fact]
        public void A_failure_is_said_with_its_reason_even_beside_a_transfer()
        {
            Assert.Equal("Save sync failed: RomM did not answer in time.",
                SaveSyncMessages.Describe(1, 0, 0, 1, "RomM did not answer in time."));
        }
    }
}
