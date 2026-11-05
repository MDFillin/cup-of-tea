package cupoftea.tv.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Button
import androidx.tv.material3.ButtonDefaults
import androidx.tv.material3.ListItem
import androidx.tv.material3.ListItemDefaults
import androidx.tv.material3.RadioButton
import androidx.tv.material3.Switch
import androidx.tv.material3.SwitchDefaults
import androidx.tv.material3.Text

@Composable
fun ScreenTitle(text: String, subtitle: String? = null) {
    Column(Modifier.padding(bottom = 16.dp)) {
        Text(text, fontSize = 32.sp, color = Palette.Text1)
        if (subtitle != null) Text(subtitle, fontSize = 16.sp, color = Palette.Text2, modifier = Modifier.padding(top = 4.dp))
    }
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(text, fontSize = 18.sp, color = Palette.Accent, modifier = modifier.padding(top = 20.dp, bottom = 8.dp))
}

@Composable
fun Hint(text: String, color: Color = Palette.Text3, modifier: Modifier = Modifier) {
    Text(text, fontSize = 14.sp, color = color, modifier = modifier.padding(vertical = 4.dp))
}

@Composable
fun rowColors() = ListItemDefaults.colors(
    containerColor = Palette.Surface,
    contentColor = Palette.Text1,
    focusedContainerColor = Palette.Text1,
    focusedContentColor = Palette.Bg,
    selectedContainerColor = Palette.Surface3,
    selectedContentColor = Palette.Text1,
)

/** Строка настройки: заголовок, пояснение, что-то справа. */
@Composable
fun SettingRow(
    title: String,
    subtitle: String? = null,
    modifier: Modifier = Modifier,
    selected: Boolean = false,
    onLongClick: (() -> Unit)? = null,
    leading: (@Composable () -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    ListItem(
        selected = selected,
        onClick = onClick,
        onLongClick = onLongClick,
        modifier = modifier.padding(vertical = 3.dp),
        headlineContent = { Text(title, fontSize = 19.sp) },
        supportingContent = subtitle?.let { { Text(it, fontSize = 14.sp) } },
        leadingContent = leading?.let { l -> { l() } },
        trailingContent = trailing,
        colors = rowColors(),
    )
}

@Composable
fun SwitchRow(title: String, subtitle: String? = null, checked: Boolean, onChange: (Boolean) -> Unit) {
    SettingRow(title, subtitle, trailing = {
        Switch(
            checked = checked, onCheckedChange = null,
            colors = SwitchDefaults.colors(checkedTrackColor = Palette.Accent, checkedThumbColor = Palette.OnAccent),
        )
    }) { onChange(!checked) }
}

@Composable
fun RadioRow(title: String, subtitle: String? = null, selected: Boolean, onSelect: () -> Unit) {
    SettingRow(title, subtitle, trailing = { RadioButton(selected = selected, onClick = null) }) { onSelect() }
}

@Composable
fun CupButton(text: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        modifier = modifier,
        colors = ButtonDefaults.colors(
            containerColor = Palette.Surface3, contentColor = Palette.Text1,
            focusedContainerColor = Palette.Accent, focusedContentColor = Palette.OnAccent,
        ),
    ) { Text(text, fontSize = 17.sp) }
}
