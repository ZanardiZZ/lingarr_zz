using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(14)]
public class M0014_AddSelectiveRetryGlossarySettings : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "selective_retry_glossary", value = "{}" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_proper_noun_lock_enabled", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_protected_patterns", value = "[]" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "selective_retry_glossary" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_proper_noun_lock_enabled" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_protected_patterns" });
    }
}
