package com.qingtoolbox.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Code
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

@Composable
fun TextCodecScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val style = LocalQingAppearance.current
    val scheme = MaterialTheme.colorScheme
    val fieldShape = androidx.compose.foundation.shape.RoundedCornerShape(style.controlCornerRadius)
    var input by rememberSaveable { mutableStateOf("") }
    var operationName by rememberSaveable { mutableStateOf(TextCodecOperation.BASE64_ENCODE.name) }
    var result by rememberSaveable { mutableStateOf<String?>(null) }
    var errorMessage by rememberSaveable { mutableStateOf<String?>(null) }
    val operation = TextCodecOperation.entries.firstOrNull { it.name == operationName }
        ?: TextCodecOperation.BASE64_ENCODE
    val fieldColors = OutlinedTextFieldDefaults.colors(
        focusedBorderColor = scheme.primary,
        unfocusedBorderColor = style.controlBorderColor,
        focusedLabelColor = scheme.primary,
        unfocusedLabelColor = scheme.onSurfaceVariant,
        cursorColor = scheme.primary,
        errorBorderColor = scheme.error,
        errorCursorColor = scheme.error,
        errorLabelColor = scheme.error,
    )

    androidx.compose.foundation.layout.Box(modifier = modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(
                        modifier = Modifier.padding(18.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            QingIconSurface(modifier = Modifier.size(44.dp)) {
                                Icon(
                                    imageVector = Icons.Outlined.Code,
                                    contentDescription = null,
                                    tint = scheme.primary,
                                )
                            }
                            Column {
                                Text(
                                    text = stringResource(R.string.text_codec_title),
                                    style = MaterialTheme.typography.titleMedium,
                                    fontWeight = FontWeight.SemiBold,
                                )
                                Text(
                                    text = stringResource(R.string.text_codec_body),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = scheme.onSurfaceVariant,
                                )
                            }
                        }
                        OutlinedTextField(
                            value = input,
                            onValueChange = {
                                input = it
                                errorMessage = null
                            },
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(156.dp),
                            label = { Text(stringResource(R.string.text_codec_input_label)) },
                            placeholder = { Text(stringResource(R.string.text_codec_input_placeholder)) },
                            minLines = 6,
                            maxLines = 6,
                            isError = errorMessage != null,
                            shape = fieldShape,
                            colors = fieldColors,
                        )
                        Text(
                            text = stringResource(R.string.text_codec_characters, input.length),
                            style = MaterialTheme.typography.labelSmall,
                            color = scheme.onSurfaceVariant,
                        )
                    }
                }
            }
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(
                        modifier = Modifier.padding(12.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text(
                            text = stringResource(R.string.text_codec_operation),
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold,
                            modifier = Modifier.padding(horizontal = 6.dp),
                        )
                        OperationRow(
                            first = TextCodecOperation.BASE64_ENCODE,
                            second = TextCodecOperation.BASE64_DECODE,
                            selected = operation,
                            onSelected = { operationName = it.name; errorMessage = null },
                        )
                        OperationRow(
                            first = TextCodecOperation.URL_ENCODE,
                            second = TextCodecOperation.URL_DECODE,
                            selected = operation,
                            onSelected = { operationName = it.name; errorMessage = null },
                        )
                    }
                }
            }
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    QingPrimaryButton(
                        onClick = {
                            try {
                                result = TextCodec.convert(operation, input)
                                errorMessage = null
                            } catch (error: TextCodecException) {
                                result = null
                                errorMessage = context.getString(error.error.messageRes())
                            }
                        },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.text_codec_convert))
                    }
                    QingSecondaryButton(
                        onClick = {
                            input = ""
                            result = null
                            errorMessage = null
                        },
                        modifier = Modifier.weight(1f),
                    ) {
                        Text(stringResource(R.string.clear))
                    }
                }
            }
            if (errorMessage != null) {
                item {
                    QingCard(modifier = Modifier.fillMaxWidth()) {
                        Text(
                            text = errorMessage.orEmpty(),
                            color = scheme.error,
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(18.dp),
                        )
                    }
                }
            }
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(
                        modifier = Modifier.padding(18.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Text(
                            text = stringResource(R.string.text_codec_result),
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold,
                        )
                        when {
                            result == null -> Text(
                                text = stringResource(R.string.text_codec_result_placeholder),
                                style = MaterialTheme.typography.bodyMedium,
                                color = scheme.onSurfaceVariant,
                            )
                            result!!.isEmpty() -> Text(
                                text = stringResource(R.string.text_codec_result_empty),
                                style = MaterialTheme.typography.bodyMedium,
                                color = scheme.onSurfaceVariant,
                            )
                            else -> OutlinedTextField(
                                value = result.orEmpty(),
                                onValueChange = {},
                                readOnly = true,
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .height(156.dp),
                                label = { Text(stringResource(R.string.text_codec_result_readonly)) },
                                minLines = 6,
                                maxLines = 6,
                                shape = fieldShape,
                                colors = fieldColors,
                            )
                        }
                        QingSecondaryButton(
                            onClick = {
                                val converted = result ?: return@QingSecondaryButton
                                copyResult(context, converted)
                                scope.launch {
                                    snackbarHostState.showSnackbar(
                                        context.getString(R.string.text_codec_result_copied),
                                    )
                                }
                            },
                            enabled = result != null,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Icon(Icons.Outlined.ContentCopy, contentDescription = null)
                            Spacer(Modifier.size(8.dp))
                            Text(stringResource(R.string.text_codec_copy_result))
                        }
                    }
                }
            }
            item {
                QingStatusText(
                    text = stringResource(R.string.text_codec_status),
                    modifier = Modifier.padding(top = 4.dp, bottom = 20.dp),
                )
            }
        }
        SnackbarHost(
            hostState = snackbarHostState,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(16.dp),
        )
    }
}

@Composable
private fun OperationRow(
    first: TextCodecOperation,
    second: TextCodecOperation,
    selected: TextCodecOperation,
    onSelected: (TextCodecOperation) -> Unit,
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        OperationCard(
            operation = first,
            selected = selected == first,
            onClick = { onSelected(first) },
            modifier = Modifier.weight(1f),
        )
        OperationCard(
            operation = second,
            selected = selected == second,
            onClick = { onSelected(second) },
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
private fun OperationCard(
    operation: TextCodecOperation,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    QingClickableCard(
        onClick = onClick,
        modifier = modifier,
        selected = selected,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 10.dp, horizontal = 8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            QingIconSurface(modifier = Modifier.size(36.dp)) {
                Icon(
                    imageVector = when (operation) {
                        TextCodecOperation.BASE64_ENCODE,
                        TextCodecOperation.BASE64_DECODE -> Icons.Outlined.Code
                        TextCodecOperation.URL_ENCODE,
                        TextCodecOperation.URL_DECODE -> Icons.Outlined.Language
                    },
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
            Text(
                text = stringResource(operation.labelRes),
                style = MaterialTheme.typography.labelLarge,
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
            )
        }
    }
}

private fun copyResult(context: Context, result: String) {
    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
    clipboard?.setPrimaryClip(ClipData.newPlainText(context.getString(R.string.text_codec_result), result))
}

private fun TextCodecError.messageRes(): Int = when (this) {
    TextCodecError.INVALID_BASE64 -> R.string.text_codec_invalid_base64
    TextCodecError.INVALID_URL_PERCENT -> R.string.text_codec_invalid_url_percent
    TextCodecError.INVALID_URL_UTF8 -> R.string.text_codec_invalid_url_utf8
}
