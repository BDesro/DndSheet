using System.Windows;
using DndSheet.App.ViewModels;
using DndSheet.Core.Domain;
using DndSheet.Core.Rules;

namespace DndSheet.App.Views;

public partial class ShortRestDialog
{
    private readonly PlayViewModel _play;
    private readonly Character _character;

    public ShortRestDialog(PlayViewModel play, Character character)
    {
        InitializeComponent();
        _play = play;
        _character = character;
        UpdateState();
    }

    private void OnSpend(object sender, RoutedEventArgs e)
    {
        var size = _character.Combat.HitDieSize;
        if (!int.TryParse(RollBox.Text, out var roll) || roll < 1 || roll > size)
        {
            Feedback.Text = $"Enter a roll from 1 to {size}.";
            RollBox.Focus();
            return;
        }
        Spend(roll);
    }

    private void OnRoll(object sender, RoutedEventArgs e) =>
        Spend(Random.Shared.Next(1, _character.Combat.HitDieSize + 1));

    private void Spend(int roll)
    {
        var before = _character.Combat.CurrentHp;
        _play.SpendHitDie(roll);
        Feedback.Text = $"Rolled {roll} on d{_character.Combat.HitDieSize}: healed {_character.Combat.CurrentHp - before}.";
        RollBox.Clear();
        UpdateState();
    }

    private void UpdateState()
    {
        HpText.Text = $"HP {_character.Combat.CurrentHp} / {CharacterRules.MaxHitPoints(_character)}";
        DiceText.Text = $"Hit dice remaining: {_character.Combat.HitDiceRemaining} of {CharacterRules.HitDiceTotal(_character)} (d{_character.Combat.HitDieSize})";
        var canSpend = _character.Combat.HitDiceRemaining > 0 && !CharacterRules.IsDead(_character);
        SpendButton.IsEnabled = RollButton.IsEnabled = RollBox.IsEnabled = canSpend;
    }

    private void OnFinish(object sender, RoutedEventArgs e) => DialogResult = true;
}
