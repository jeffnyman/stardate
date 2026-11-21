using System.Globalization;
using System.Text.RegularExpressions;

namespace Stardate.Driver;

/// <summary>
/// <i>The Kobayashi Alternative</i>'s Take menu (F4) and inventory (F2), worked by reading them:
/// a menu key pressed before the menu is drawn can pick the wrong thing, or a thing with no name.
/// </summary>
internal sealed partial class KobayashiMenus(Driver driver)
{
    private const byte F1 = 0x3B, F2 = 0x3C, F4 = 0x3E;

    /// <summary>
    /// <c>{take:NAME}</c> takes a thing from the Take menu; <c>{pack:NAME}</c> and
    /// <c>{hand:NAME}</c> move it to the pack or the hands, if it isn't there already;
    /// <c>{wear:NAME}</c> changes clothes (in the transporter room) until those are worn.
    /// </summary>
    public void Menu(string command, string name)
    {
        switch (command)
        {
            case "take":
                Open(F4, "Take");
                var line = driver.ScreenText().Select(l => Regex.Match(l, @"F(\d+) " + Regex.Escape(name))).FirstOrDefault(m => m.Success);
                if (line is null)
                {
                    driver.Output($"[*** no {name} to take]");
                }
                else
                {
                    Choose(int.Parse(line.Groups[1].Value, CultureInfo.InvariantCulture));
                }

                Close("Take");
                break;

            case "wear":
                Open(F2, "Inventory");
                for (int k = 0; k < 8 && !driver.Shows("F1 " + name); k++)
                {
                    Choose(1);
                }

                if (!driver.Shows("F1 " + name))
                {
                    driver.Output($"[*** can't wear {name}]");
                }

                Close("Inventory");
                break;

            default:
                Open(F2, "Inventory");
                int key = 0;
                bool inPack = false;
                foreach (var screenLine in driver.ScreenText())
                {
                    foreach (Match item in InventoryItem().Matches(screenLine))
                    {
                        if (item.Groups[2].Value.Trim() == name && item.Groups[1].Value != "1")
                        {
                            key = int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture);
                            // the pack is the box's left column, the hands its right
                            inPack = item.Index < 19;
                        }
                    }
                }

                if (key == 0)
                {
                    driver.Output($"[*** no {name} in the inventory]");
                }
                else if ((command == "pack") != inPack)
                {
                    Choose(key);
                }

                Close("Inventory");
                break;
        }
    }

    // a function key in a menu: F1 is 3Bh, and on
    private void Choose(int number)
    {
        driver.Press((byte)(F1 - 1 + number));
        driver.RunSeconds(1);
        driver.WaitUntilReady();
    }

    private void Open(byte key, string title)
    {
        driver.Press(key);
        for (int k = 0; k < 40 && !driver.Shows("┌ " + title); k++)
        {
            driver.RunSeconds(0.1);
        }

        driver.RunSeconds(0.5);
        driver.WaitUntilReady();
    }

    private void Close(string title)
    {
        for (int k = 0; k < 4 && driver.Shows("┌ " + title); k++)
        {
            driver.Press(Keys.Escape);
            driver.RunSeconds(0.5);
            driver.WaitUntilReady();
        }
    }

    [GeneratedRegex(@"F(\d+) ([a-zA-Z][a-zA-Z ]*?)(?=\s{2,}|│|$)")]
    private static partial Regex InventoryItem();
}
