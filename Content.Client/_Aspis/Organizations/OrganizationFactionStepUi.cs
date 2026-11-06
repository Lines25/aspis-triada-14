using System;
using System.Collections.Generic;
using System.Text;
using Content.Shared._Aspis.Organizations;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface;
using Robust.Client.Graphics;
using Robust.Shared.Maths;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// Read-only expandable task checklist shared by the organization console and PDA.
/// All completion flags arrive from the server: clicking a row changes presentation only.
/// </summary>
public static class OrganizationFactionStepUi
{
    private static readonly Color Active = Color.FromHex("#79E0A0");
    private static readonly Color Finished = Color.FromHex("#85949E");
    private static readonly Color Waiting = Color.FromHex("#BFA56B");
    private static readonly Color Locked = Color.FromHex("#727C88");

    public static void AddTo(BoxContainer parent, OrganizationFactionStepEntry[]? steps,
        string code, HashSet<string> expandedLists, HashSet<string> expandedDetails, bool compact)
    {
        if (steps == null || steps.Length == 0)
            return;

        var firstOpen = Array.FindIndex(steps, step => !step.Completed);
        var finishedCount = steps.Length - CountIncomplete(steps);
        var listKey = "list:" + code;
        var isOpen = expandedLists.Contains(listKey);

        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex(compact ? "#1D2C33" : "#202932")
            }
        };
        var main = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 5,
            Margin = new Thickness(compact ? 6 : 9)
        };
        panel.AddChild(main);
        var toggle = new Button
        {
            Text = (isOpen ? "▾ " : "▸ ") + $"Етапи завдання ({finishedCount}/{steps.Length})",
            HorizontalExpand = true,
            MinHeight = compact ? 28 : 31
        };
        main.AddChild(toggle);
        var list = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = compact ? 3 : 5,
            Visible = isOpen
        };
        main.AddChild(list);
        toggle.OnPressed += _ =>
        {
            list.Visible = !list.Visible;
            if (list.Visible) expandedLists.Add(listKey);
            else expandedLists.Remove(listKey);
            toggle.Text = (list.Visible ? "▾ " : "▸ ") +
                $"Етапи завдання ({finishedCount}/{steps.Length})";
        };

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var isCurrent = i == firstOpen;
            // Before the player finishes the first stage, later stages stay gray.
            // Once work begins, the unstarted stages become muted amber.
            var color = step.Completed ? Finished : isCurrent ? Active :
                firstOpen == 0 ? Locked : Waiting;
            var key = $"step:{code}:{i}";
            var detailsOpen = expandedDetails.Contains(key);
            var row = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 6
            };
            row.AddChild(new Label
            {
                Text = "●",
                FontColorOverride = color,
                MinWidth = compact ? 12 : 15
            });
            var title = step.Completed ? CrossOut(step.Title) : step.Title;
            var button = new Button
            {
                Text = (detailsOpen ? "▾ " : "▸ ") + Wrap(title, compact ? 25 : 37),
                HorizontalExpand = true,
                MinHeight = compact ? 26 : 30,
                Modulate = color
            };
            row.AddChild(button);
            list.AddChild(row);
            var explanation = new PanelContainer
            {
                HorizontalExpand = true,
                Visible = detailsOpen,
                PanelOverride = new StyleBoxFlat
                {
                    BackgroundColor = Color.FromHex("#2A353C")
                }
            };
            explanation.AddChild(new Label
            {
                Text = Wrap(step.Details, compact ? 29 : 54),
                Margin = new Thickness(8),
                FontColorOverride = Color.FromHex("#CBD6DC")
            });
            list.AddChild(explanation);
            button.OnPressed += _ =>
            {
                explanation.Visible = !explanation.Visible;
                if (explanation.Visible) expandedDetails.Add(key);
                else expandedDetails.Remove(key);
                button.Text = (explanation.Visible ? "▾ " : "▸ ") +
                    Wrap(title, compact ? 25 : 37);
            };
        }
        parent.AddChild(panel);
    }

    private static int CountIncomplete(OrganizationFactionStepEntry[] steps)
    {
        var count = 0;
        foreach (var step in steps)
            if (!step.Completed) count++;
        return count;
    }

    private static string CrossOut(string source)
    {
        var result = new StringBuilder();
        foreach (var ch in source)
        {
            result.Append(ch);
            if (!char.IsWhiteSpace(ch))
                result.Append('\u0336');
        }
        return result.ToString();
    }

    private static string Wrap(string text, int width)
    {
        var result = new StringBuilder();
        var col = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (col > 0 && col + word.Length + 1 > width)
            {
                result.Append('\n');
                col = 0;
            }
            else if (col > 0)
            {
                result.Append(' ');
                col++;
            }
            result.Append(word);
            col += word.Length;
        }
        return result.ToString();
    }
}
