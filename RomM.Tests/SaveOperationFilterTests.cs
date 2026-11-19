using System;
using System.Collections.Generic;
using System.Linq;
using RomM.Models.RomM.Save;
using RomM.Saves;
using Xunit;

namespace RomM.Tests
{
    public class SaveOperationFilterTests
    {
        private const string Name = "Snipperclips [01008E20047DC000].zip";

        private static RomMSyncOperation Op(string action, string file, string slot = "autosave", int day = 1, int rom = 7, int id = 0)
        {
            return new RomMSyncOperation
            {
                Action = action, RomId = rom, FileName = file, Slot = slot, SaveId = id,
                ServerUpdatedAt = new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc),
            };
        }

        private static List<RomMSyncOperation> Pick(params RomMSyncOperation[] ops)
        {
            List<RomMSyncOperation> passed;
            return SaveOperationFilter.ForTarget(ops, 7, Name, "autosave", out passed);
        }

        // Two saves RomM holds for one ROM were both downloaded into the one local save, and the
        // one listed last won -- an older copy over the newer one.
        [Fact]
        public void A_save_under_another_name_is_not_applied_to_this_one()
        {
            var ours = Op(RomMSyncAction.Download, Name, day: 16, id: 1);
            var other = Op(RomMSyncAction.Download, "Snipperclips.zip", day: 18, id: 2);

            Assert.Equal(new[] { ours }, Pick(ours, other));
        }

        // RomM renames an upload with its arrival time; Tender's uploads all reach the PC that way.
        [Theory]
        [InlineData("Snipperclips [01008E20047DC000] [2026-11-19_03-13-12].zip")]
        [InlineData("Snipperclips [01008E20047DC000] [2026-07-19 13-41-44-611].zip")]
        public void A_save_renamed_with_romms_upload_tag_is_this_save(string tagged)
        {
            var op = Op(RomMSyncAction.Download, tagged, id: 41);

            Assert.Equal(new[] { op }, Pick(op));
        }

        [Fact]
        public void A_bracketed_title_id_is_not_taken_for_an_upload_tag()
        {
            Assert.Equal("Snipperclips [01008E20047DC000][JP][v0].zip",
                SaveOperationFilter.Untagged("Snipperclips [01008E20047DC000][JP][v0] [2026-11-19_03-13-12].zip"));
            Assert.Equal("Game [2026-11-19].zip", SaveOperationFilter.Untagged("Game [2026-11-19].zip"));
        }

        [Fact]
        public void Another_slot_is_not_applied()
        {
            var ours = Op(RomMSyncAction.Download, Name, id: 1);
            var otherSlot = Op(RomMSyncAction.Download, Name, slot: "slot2", id: 2);

            Assert.Equal(new[] { ours }, Pick(otherSlot, ours));
        }

        [Fact]
        public void Of_several_downloads_of_this_save_only_the_newest_is_applied()
        {
            var older = Op(RomMSyncAction.Download, Name, day: 16, id: 1);
            var newer = Op(RomMSyncAction.Conflict, Name.ToUpperInvariant(), day: 18, id: 2);

            Assert.Equal(new[] { newer }, Pick(newer, older));
        }

        [Fact]
        public void An_upload_of_the_save_reported_is_kept_and_other_roms_are_left_out()
        {
            var upload = Op(RomMSyncAction.Upload, null);
            var otherRom = Op(RomMSyncAction.Download, Name, rom: 8);

            List<RomMSyncOperation> passed;
            var chosen = SaveOperationFilter.ForTarget(new[] { upload, otherRom }, 7, Name, "autosave", out passed);

            Assert.Equal(new[] { upload }, chosen);
            Assert.Empty(passed);
        }
    }
}
