/*
 * Emby Backup settings page controller.
 *
 * The plugin GUID must match Plugin.cs. The configuration keys must match PluginConfiguration.cs exactly.
 * The page polls /EmbyBackup/Status while it is open; a backup itself runs in the background on the server.
 */
define(['baseView', 'loading'], function (BaseView, loading) {
    'use strict';

    var pluginId = '5a3c44fa-8c66-4f07-b75e-4a8c6123852f';

    function el(tag, className, text) {
        var e = document.createElement(tag);
        if (className) {
            e.className = className;
        }
        if (text !== undefined && text !== null) {
            e.textContent = text;
        }
        return e;
    }

    function q(view, id) {
        return view.querySelector('#' + id);
    }

    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function toast(text) {
        require(['toast'], function (t) {
            (t.default || t)(text);
        });
    }

    function fmtBytes(bytes) {
        var value = Number(bytes) || 0;
        if (value < 1024) {
            return value + ' B';
        }
        var units = ['KB', 'MB', 'GB', 'TB'];
        var index = -1;
        do {
            value /= 1024;
            index++;
        } while (value >= 1024 && index < units.length - 1);
        return value.toFixed(value >= 10 ? 0 : 1) + ' ' + units[index];
    }

    function fmtDate(utc) {
        if (!utc) {
            return '\u2014';
        }
        var date = new Date(utc);
        return isNaN(date.getTime()) ? String(utc) : date.toLocaleString();
    }

    function stateText(state) {
        switch (state) {
            case 'running': return 'Running';
            case 'success': return 'Completed';
            case 'failed': return 'Failed';
            case 'cancelled': return 'Cancelled';
            default: return 'Idle';
        }
    }

    function stateClass(state) {
        if (state === 'running') { return 'ebRunning'; }
        if (state === 'success') { return 'ebSuccess'; }
        if (state === 'failed' || state === 'cancelled') { return 'ebFailed'; }
        return '';
    }

    // Changes as soon as a new backup is recorded, even if it finished between two polls. The lists are
    // reloaded on a change so a fast run cannot leave the history and file tables showing stale data.
    function statusSignature(status) {
        var last = status && status.Last;
        return (status && status.State || '') + '|' + (last
            ? (last.DateUtc || '') + '|' + (last.Status || '') + '|' + (last.FileName || '')
            : '');
    }

    function setDirty(view, dirty) {
        view.querySelector('#ebDirty').textContent = dirty
            ? 'You have unsaved changes.'
            : 'All changes are saved.';
    }

    function loadSettings(view) {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            q(view, 'ebDestinationType').value = config.DestinationType || 'Local';
            q(view, 'ebDestination').value = config.DestinationPath || '';
            q(view, 'ebNetworkPath').value = config.NetworkPath || '';
            q(view, 'ebNetworkUser').value = config.NetworkUsername || '';
            q(view, 'ebNetworkPassword').value = config.NetworkPassword || '';
            q(view, 'ebNetworkDomain').value = config.NetworkDomain || '';
            q(view, 'ebFtpHost').value = config.FtpHost || '';
            q(view, 'ebFtpPort').value = config.FtpPort || 21;
            q(view, 'ebFtpPath').value = config.FtpPath || '/';
            q(view, 'ebFtpUser').value = config.FtpUsername || '';
            q(view, 'ebFtpPassword').value = config.FtpPassword || '';
            q(view, 'ebFtpTls').checked = !!config.FtpUseTls;
            q(view, 'ebFtpPassive').checked = config.FtpPassive !== false;
            q(view, 'ebFtpCert').checked = !!config.FtpAcceptAnyCertificate;
            q(view, 'ebRetention').value = config.RetentionCount || 7;
            q(view, 'ebCompression').value = config.Compression || 'Optimal';
            q(view, 'ebBusySeconds').value = config.BusyTimeoutSeconds || 60;
            q(view, 'ebSchedulePeriod').value = config.SchedulePeriod || 'Daily';
            q(view, 'ebScheduleDay').value = config.ScheduleDayOfWeek || 'Monday';
            q(view, 'ebScheduleTime').value = config.ScheduleTime || '03:00';
            q(view, 'ebScheduled').checked = config.ScheduledBackupsEnabled !== false;
            q(view, 'ebVerify').checked = config.VerifyAfterCreate !== false;
            q(view, 'ebProtect').checked = !!config.ProtectBackupsWithPassword;
            q(view, 'ebBackupPassword').value = config.BackupPassword || '';
            updateDestinationFields(view);
            updateScheduleFields(view);
            updateProtectionFields(view);
            setDirty(view, false);
        });
    }

    function saveSettings(view) {
        var protect = q(view, 'ebProtect').checked;
        var password = q(view, 'ebBackupPassword').value;
        if (protect && !password) {
            showModal(view, 'Backup password required',
                'Password protection is enabled, but the backup password is empty.\n\n'
                + 'Enter a password or turn password protection off before saving.');
            return Promise.reject({ ebHandled: true });
        }

        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.DestinationType = q(view, 'ebDestinationType').value;
            config.DestinationPath = q(view, 'ebDestination').value.trim();
            config.NetworkPath = q(view, 'ebNetworkPath').value.trim();
            config.NetworkUsername = q(view, 'ebNetworkUser').value.trim();
            config.NetworkPassword = q(view, 'ebNetworkPassword').value;
            config.NetworkDomain = q(view, 'ebNetworkDomain').value.trim();
            config.FtpHost = q(view, 'ebFtpHost').value.trim();
            config.FtpPort = Math.max(1, Math.min(65535, parseInt(q(view, 'ebFtpPort').value, 10) || 21));
            config.FtpPath = q(view, 'ebFtpPath').value.trim() || '/';
            config.FtpUsername = q(view, 'ebFtpUser').value.trim();
            config.FtpPassword = q(view, 'ebFtpPassword').value;
            config.FtpUseTls = q(view, 'ebFtpTls').checked;
            config.FtpPassive = q(view, 'ebFtpPassive').checked;
            config.FtpAcceptAnyCertificate = q(view, 'ebFtpCert').checked;
            config.RetentionCount = Math.max(1, Math.min(365, parseInt(q(view, 'ebRetention').value, 10) || 7));
            config.Compression = q(view, 'ebCompression').value;
            config.BusyTimeoutSeconds = Math.max(5, Math.min(600, parseInt(q(view, 'ebBusySeconds').value, 10) || 60));
            config.SchedulePeriod = q(view, 'ebSchedulePeriod').value;
            config.ScheduleDayOfWeek = q(view, 'ebScheduleDay').value;
            config.ScheduleTime = q(view, 'ebScheduleTime').value || '03:00';
            config.ScheduledBackupsEnabled = q(view, 'ebScheduled').checked;
            config.VerifyAfterCreate = q(view, 'ebVerify').checked;
            config.ProtectBackupsWithPassword = protect;
            config.BackupPassword = protect ? password : '';
            if (!protect) {
                q(view, 'ebBackupPassword').value = '';
            }

            return ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                setDirty(view, false);
                return result;
            });
        });
    }

    function updateProtectionFields(view) {
        var enabled = q(view, 'ebProtect').checked;
        var input = q(view, 'ebBackupPassword');
        input.disabled = !enabled;
        if (!enabled) {
            input.value = '';
        }
    }

    function updateDestinationFields(view) {
        var type = q(view, 'ebDestinationType').value;

        q(view, 'ebLocalFields').hidden = type !== 'Local';
        q(view, 'ebNetworkFields').hidden = type !== 'Network';
        q(view, 'ebFtpFields').hidden = type !== 'Ftp';
        q(view, 'ebCredentialNote').hidden = type === 'Local';

        serverIsWindows().then(function (windows) {
            var path = q(view, 'ebNetworkPath');
            path.placeholder = windows ? '\\\\nas\\backups\\emby' : '/mnt/nas/emby';
            q(view, 'ebNetworkBrowse').hidden = windows;
            q(view, 'ebNetworkCredentials').hidden = !windows;
            q(view, 'ebNetworkHint').textContent = windows
                ? 'UNC path, for example \\\\nas\\backups\\emby. Leave the credentials empty when the '
                    + 'Windows session that runs Emby can already write to the share.'
                : 'Mount the SMB share in the operating system (for example with cifs in /etc/fstab) and enter '
                    + 'the mount point here. Emby cannot mount shares by itself.';
        });
    }

    // Mirrors BackupScheduleRules.Describe on the server; the hint explains what Emby can and cannot do when
    // the server is off at the scheduled time.
    function updateScheduleFields(view) {
        var period = q(view, 'ebSchedulePeriod').value || 'Daily';
        var day = q(view, 'ebScheduleDay').value || 'Monday';
        var time = q(view, 'ebScheduleTime').value || '03:00';
        var enabled = q(view, 'ebScheduled').checked;
        var hint;

        q(view, 'ebScheduleDayField').hidden = period !== 'Weekly';

        if (period === 'Weekly') {
            hint = 'Automatic backups run every ' + day + ' at ' + time + ' (server local time). '
                + 'If Emby is not running at that moment, that week is skipped.';
        } else if (period === 'Monthly') {
            hint = 'Automatic backups run on the first day of each month at ' + time + ' (server local time). '
                + 'If Emby is off that day, the backup runs on the next day at the scheduled time.';
        } else if (period === 'AlternateDays') {
            hint = 'Automatic backups run every other day at ' + time + ' (server local time), counted from '
                + 'the last successful automatic backup.';
        } else {
            hint = 'Automatic backups run every day at ' + time + ' (server local time). '
                + 'If Emby is off at that time, that day is skipped.';
        }

        q(view, 'ebScheduleHint').textContent = enabled
            ? hint
            : 'Automatic backups are turned off. Manual backups can still be started from this page.';
    }

    function renderStatus(view, status) {
        var state = status.State || 'idle';
        var dot = q(view, 'ebStateDot');
        dot.className = 'ebDot ' + stateClass(state);
        q(view, 'ebState').textContent = stateText(state);
        q(view, 'ebBar').style.width = Math.max(0, Math.min(100, status.Progress || 0)) + '%';
        q(view, 'ebMessage').textContent = status.Message || '';

        var started = q(view, 'ebStarted');
        started.textContent = fmtDate(status.StartedUtc);
        q(view, 'ebFinished').textContent = fmtDate(status.FinishedUtc);

        var destination = q(view, 'ebDestinationStatus');
        destination.textContent = status.DestinationPath || '\u2014';
        destination.title = status.DestinationPath || '';

        var last = status.Last;
        q(view, 'ebLastBackup').textContent = last
            ? fmtDate(last.DateUtc) + ' (' + stateText(last.Status) + ')'
            : '\u2014';

        var warnings = q(view, 'ebWarnings');
        warnings.textContent = '';
        (status.Warnings || []).slice(0, 8).forEach(function (text) {
            warnings.appendChild(el('div', 'ebWarn', text));
        });

        var restore = status.Restore;
        var restoreRunning = !!(restore && restore.Running);
        q(view, 'ebRun').disabled = !!status.Running || restoreRunning;
        q(view, 'ebCancel').disabled = !status.Running;
        q(view, 'ebRunHint').textContent = restoreRunning
            ? 'A restore is running. Wait for it to finish before starting a backup.'
            : (status.Running
                ? 'A backup is running. You can keep this page open or close it; the server continues.'
                : 'The settings are saved automatically before a manual run.');
        if (status.PluginVersion) {
            q(view, 'ebPluginVersion').textContent = 'Plugin ' + status.PluginVersion;
            q(view, 'ebFooterVersion').textContent = 'Emby Backup ' + status.PluginVersion;
        }

        renderRestore(view, restore);
    }

    function protectionBadge(encrypted, known) {
        if (known === false) {
            return el('span', 'ebBadge', 'Unknown');
        }
        return el('span', 'ebBadge' + (encrypted ? ' ebWarn' : ''), encrypted ? 'Password' : 'None');
    }

    function statusRow(badge, ok) {
        var span = el('span', 'ebBadge' + (ok ? ' ebOk' : ''), badge);
        return span;
    }

    function renderHistory(view, records) {
        var body = q(view, 'ebHistory');
        body.textContent = '';
        q(view, 'ebHistoryEmpty').hidden = records.length > 0;
        q(view, 'ebHistoryWrap').hidden = records.length === 0;

        records.forEach(function (record) {
            var row = el('tr');

            row.appendChild(el('td', null, fmtDate(record.DateUtc)));
            row.appendChild(el('td', 'ebName', record.FileName || '\u2014'));
            row.appendChild(el('td', null, record.Bytes ? fmtBytes(record.Bytes) : '\u2014'));
            row.appendChild(el('td', null,
                (record.FileCount || 0) + ' files / ' + (record.DatabaseCount || 0) + ' DB'));

            var statusCell = el('td');
            statusCell.appendChild(el('span', 'ebStatus eb' + (record.Status || '').charAt(0).toUpperCase()
                + (record.Status || '').slice(1), stateText(record.Status)));
            if (record.Verified) {
                statusCell.appendChild(document.createTextNode(' '));
                statusCell.appendChild(statusRow('verified', true));
            }
            if (record.Error) {
                statusCell.appendChild(el('div', 'ebError ebSmall', record.Error));
            }
            row.appendChild(statusCell);

            var protection = el('td');
            protection.appendChild(protectionBadge(!!record.Encrypted, true));
            row.appendChild(protection);

            row.appendChild(el('td', null, record.Origin === 'scheduled' ? 'Automatic' : 'Manual'));

            var actions = el('td');
            if (record.FileName) {
                var verify = el('button', 'ebBtn ebSmall', 'Verify');
                verify.type = 'button';
                verify.addEventListener('click', function () {
                    verifyFile(view, record.FileName);
                });
                actions.appendChild(verify);
            }
            row.appendChild(actions);
            body.appendChild(row);
        });
    }

    function renderFiles(view, result) {
        var body = q(view, 'ebFiles');
        body.textContent = '';
        var files = result.Files || [];
        view.__ebFiles = files;
        var note = q(view, 'ebFilesNote');
        note.textContent = result.Error
            ? result.Error
            : (result.DestinationPath
                ? (result.DestinationType ? result.DestinationType + ': ' : 'Destination: ') + result.DestinationPath
                : 'No destination configured.');

        q(view, 'ebFilesEmpty').hidden = files.length > 0;
        q(view, 'ebFilesWrap').hidden = files.length === 0;

        files.forEach(function (file) {
            var row = el('tr');
            row.appendChild(el('td', null, fmtDate(file.CreatedUtc || file.LastWriteUtc)));
            row.appendChild(el('td', 'ebName', file.FileName));
            row.appendChild(el('td', null, fmtBytes(file.Bytes)));
            row.appendChild(el('td', null,
                file.MetadataKnown && file.HasManifest
                    ? ((file.FileCount || 0) + ' files / ' + (file.DatabaseCount || 0) + ' DB')
                    : '\u2014'));

            var manifest = el('td');
            if (!file.MetadataKnown) {
                manifest.appendChild(statusRow('Not checked', false));
            } else {
                manifest.appendChild(statusRow(file.IsEmbyBackup ? 'Emby Backup' : 'Unknown', file.IsEmbyBackup));
            }
            if (file.Error) {
                manifest.appendChild(el('div', 'ebError ebSmall', file.Error));
            }
            row.appendChild(manifest);

            var protection = el('td');
            protection.appendChild(protectionBadge(!!file.Encrypted, file.EncryptedKnown));
            row.appendChild(protection);

            row.appendChild(el('td', null, file.HasChecksumFile ? 'Present' : 'Missing'));

            var actions = el('td');
            var verify = el('button', 'ebBtn ebSmall', 'Verify');
            verify.type = 'button';
            verify.addEventListener('click', function () {
                verifyFile(view, file.FileName);
            });
            actions.appendChild(verify);

            var restore = el('button', 'ebBtn ebSmall', 'Restore');
            restore.type = 'button';
            restore.addEventListener('click', function () {
                var select = q(view, 'ebRestoreFile');
                select.value = file.FileName;
                updateRestorePasswordVisibility(view);
                startRestoreFlow(view, file.FileName);
            });
            actions.appendChild(document.createTextNode(' '));
            actions.appendChild(restore);

            row.appendChild(actions);
            body.appendChild(row);
        });

        fillRestoreFiles(view, files);
    }

    function fillRestoreFiles(view, files) {
        var select = q(view, 'ebRestoreFile');
        var previous = select.value;
        select.textContent = '';

        if (!files.length) {
            var empty = el('option', null, 'No archives found in the destination');
            empty.value = '';
            select.appendChild(empty);
            select.disabled = true;
            q(view, 'ebRestoreStart').disabled = true;
            updateRestorePasswordVisibility(view);
            return;
        }

        select.disabled = false;
        files.forEach(function (file) {
            var option = el('option', null,
                fmtDate(file.CreatedUtc || file.LastWriteUtc) + '  \u00b7  ' + file.FileName
                    + (file.EncryptedKnown && file.Encrypted ? '  (password)' : ''));
            option.value = file.FileName;
            select.appendChild(option);
        });

        var stillThere = files.some(function (file) { return file.FileName === previous; });
        if (stillThere) {
            select.value = previous;
        }

        q(view, 'ebRestoreStart').disabled = !select.value;
        updateRestorePasswordVisibility(view);
    }

    function selectedRestoreFile(view) {
        var name = q(view, 'ebRestoreFile').value;
        return (view.__ebFiles || []).filter(function (file) {
            return file.FileName === name;
        })[0] || null;
    }

    function updateRestorePasswordVisibility(view) {
        var file = selectedRestoreFile(view);
        var field = q(view, 'ebRestorePasswordField');
        var protectedArchive = !!(file && file.EncryptedKnown !== false && file.Encrypted);
        field.hidden = !protectedArchive;
        if (!protectedArchive) {
            q(view, 'ebRestorePassword').value = '';
        }
    }

    function loadStatus(view) {
        return ApiClient.getJSON(ApiClient.getUrl('EmbyBackup/Status')).then(function (status) {
            renderStatus(view, status);
            if (status.Restore && status.Restore.Running) {
                startRestorePolling(view);
            }
            return status;
        });
    }

    function loadHistory(view) {
        return ApiClient.getJSON(ApiClient.getUrl('EmbyBackup/History')).then(function (records) {
            renderHistory(view, records || []);
        });
    }

    function loadFiles(view) {
        return ApiClient.getJSON(ApiClient.getUrl('EmbyBackup/Files')).then(function (result) {
            renderFiles(view, result || {});
        });
    }

    function showModal(view, title, text) {
        q(view, 'ebModalTitle').textContent = title;
        q(view, 'ebModalBody').textContent = text;
        q(view, 'ebModal').hidden = false;
        q(view, 'ebModalClose').focus();
    }

    function hideModal(view) {
        q(view, 'ebModal').hidden = true;
    }

    // --- Folder browser ---------------------------------------------------
    // Uses Emby's own admin API, so it lists the folders Emby can see (container paths in Docker,
    // drive letters on Windows, mounted volumes on Linux/NAS).

    var FOLDER_ICON_PATH = 'M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z';
    var windowsPromise = null;

    function folderIcon() {
        var ns = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        var path = document.createElementNS(ns, 'path');
        path.setAttribute('d', FOLDER_ICON_PATH);
        svg.appendChild(path);
        return svg;
    }

    function serverIsWindows() {
        if (!windowsPromise) {
            windowsPromise = ApiClient.getJSON(ApiClient.getUrl('System/Info')).then(function (info) {
                return String(info.OperatingSystem || '').toLowerCase().indexOf('windows') >= 0;
            }, function () {
                windowsPromise = null;
                return false;
            });
        }
        return windowsPromise;
    }

    function setBrowseError(view, text) {
        var node = q(view, 'ebBrowseError');
        node.textContent = text || '';
        node.hidden = !text;
    }

    function openBrowse(view, targetId) {
        var target = targetId || 'ebDestination';
        view.__ebBrowseTarget = target;
        var start = q(view, target).value.trim();
        q(view, 'ebBrowseList').textContent = '';
        q(view, 'ebBrowsePath').textContent = '';
        q(view, 'ebBrowseUse').disabled = true;
        setBrowseError(view, '');
        q(view, 'ebBrowseModal').hidden = false;
        q(view, 'ebBrowseModal').querySelector('.ebBrowseBox').focus();

        if (start) {
            showFolder(view, start, loadRoots);
        } else {
            loadRoots(view);
        }
    }

    function closeBrowse(view) {
        q(view, 'ebBrowseModal').hidden = true;
        q(view, 'ebBrowseList').textContent = '';
        view.__ebBrowsePath = null;
    }

    function loadRoots(view) {
        serverIsWindows().then(function (windows) {
            if (!windows) {
                // Unix has a single root; the drives API is not useful there.
                showFolder(view, '/', null);
                return;
            }

            view.__ebBrowsePath = null;
            q(view, 'ebBrowsePath').textContent = 'This computer';
            q(view, 'ebBrowseUse').disabled = true;
            q(view, 'ebBrowseList').textContent = '';
            setBrowseError(view, '');

            ApiClient.getJSON(ApiClient.getUrl('Environment/Drives')).then(function (entries) {
                renderFolders(view, null, entries || []);
            }, function () {
                setBrowseError(view, 'Could not list the drives on the server.');
            });
        });
    }

    function showFolder(view, path, onError) {
        var list = q(view, 'ebBrowseList');
        list.textContent = '';
        list.appendChild(el('div', 'ebFolderEmpty', 'Loading\u2026'));

        ApiClient.getJSON(ApiClient.getUrl('Environment/DirectoryContents', {
            Path: path,
            IncludeFiles: false,
            IncludeDirectories: true
        })).then(function (entries) {
            setBrowseError(view, '');
            renderFolders(view, path, entries || []);
        }, function () {
            list.textContent = '';
            if (onError) {
                onError(view);
                return;
            }
            setBrowseError(view, 'This folder cannot be read. It may not exist or Emby may not have access to it.');
            list.appendChild(el('div', 'ebFolderEmpty', 'No folders to show.'));
        });
    }

    function renderFolders(view, path, entries) {
        var list = q(view, 'ebBrowseList');
        list.textContent = '';
        view.__ebBrowsePath = path;
        q(view, 'ebBrowsePath').textContent = path === null ? 'This computer' : path;
        q(view, 'ebBrowseUse').disabled = path === null;

        var folders = entries.filter(function (entry) {
            return entry && entry.Path && entry.Type !== 'File';
        });

        folders.sort(function (a, b) {
            var left = String(a.Name || a.Path);
            var right = String(b.Name || b.Path);
            return left.localeCompare(right, undefined, { sensitivity: 'base' });
        });

        if (path !== null) {
            var up = el('button', 'ebFolderRow ebFolderUp', '..  (parent folder)');
            up.type = 'button';
            up.addEventListener('click', function () { goUp(view); });
            list.appendChild(up);
        }

        folders.forEach(function (entry) {
            var row = el('button', 'ebFolderRow');
            row.type = 'button';
            row.appendChild(folderIcon());
            row.appendChild(el('span', null, entry.Name || entry.Path));
            row.title = entry.Path;
            row.addEventListener('click', function () { showFolder(view, entry.Path, null); });
            list.appendChild(row);
        });

        if (!folders.length) {
            list.appendChild(el('div', 'ebFolderEmpty',
                path === null ? 'No drives were found.' : 'No subfolders. You can still use this folder.'));
        }
    }

    // Emby's /Environment/ParentPath returns plain text, which ApiClient.getJSON refuses to parse.
    // Computing the parent from the path string works the same on Linux, Windows drives and UNC shares.
    function parentFolder(path) {
        if (!path) {
            return null;
        }

        var trimmed = path.replace(/[\/\\]+$/, '');
        if (!trimmed) {
            return null;
        }

        var index = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
        if (index < 0) {
            return null;
        }

        // "C:\Backups" -> "C:\"
        if (index === 2 && /^[A-Za-z]:/.test(trimmed)) {
            return trimmed.substring(0, 3);
        }

        var parent = trimmed.substring(0, index);
        // "\\nas\share" is the top of a UNC path: show the drive list instead.
        if (/^\\\\[^\\]+$/.test(parent)) {
            return null;
        }

        return parent || null;
    }

    function goUp(view) {
        var path = view.__ebBrowsePath;
        if (!path) {
            return;
        }

        var parent = parentFolder(path);
        if (parent) {
            showFolder(view, parent, null);
        } else {
            loadRoots(view);
        }
    }

    function useCurrentFolder(view) {
        var path = view.__ebBrowsePath;
        if (!path) {
            return;
        }
        var target = view.__ebBrowseTarget || 'ebDestination';
        q(view, target).value = path;
        setDirty(view, true);
        closeBrowse(view);
    }

    function verifyFile(view, fileName, password) {
        postJson('EmbyBackup/Verify', { FileName: fileName, Password: password || '' }).then(function (result) {
            if (result.PasswordRequired || result.WrongPassword) {
                var prompt = result.WrongPassword
                    ? 'The password is incorrect. Try again.'
                    : (result.Error || 'This backup is protected by a password.');
                promptPassword(view, result.WrongPassword ? 'Wrong password' : 'Password required', prompt)
                    .then(function (entered) {
                        verifyFile(view, fileName, entered);
                    }, function () { });
                return;
            }

            var lines = [];
            if (result.Valid) {
                lines.push('The archive is valid.');
            } else {
                lines.push('The archive did NOT pass verification.');
            }
            if (result.Error) {
                lines.push('Error: ' + result.Error);
            }
            lines.push('');
            lines.push('Archive: ' + fileName);
            if (result.Encrypted) { lines.push('Protection: password (AES-256)'); }
            if (result.CheckedFiles || result.CheckedDatabases) {
                lines.push('Checked ' + result.CheckedFiles + ' files and ' + result.CheckedDatabases + ' databases.');
            }
            if (result.CreatedUtc) { lines.push('Created: ' + fmtDate(result.CreatedUtc)); }
            if (result.EmbyVersion) { lines.push('Emby version: ' + result.EmbyVersion); }
            if (result.PluginVersion) { lines.push('Plugin version: ' + result.PluginVersion); }
            if (result.SourceProgramDataPath) { lines.push('Source: ' + result.SourceProgramDataPath); }
            if (result.TotalBytes) { lines.push('Uncompressed data: ' + fmtBytes(result.TotalBytes)); }
            showModal(view, result.Valid ? 'Archive verified' : 'Verification failed', lines.join('\n'));
        }, function () {
            toast('Could not verify the archive.');
        });
    }

    // --- Restore ---------------------------------------------------------
    // The server checks the archive and the password first, then the page asks for two confirmations.
    // The restore itself runs in the background and the page polls /EmbyBackup/RestoreStatus.

    function promptPassword(view, title, message) {
        return new Promise(function (resolve, reject) {
            view.__ebPasswordResolve = resolve;
            view.__ebPasswordReject = reject;
            q(view, 'ebPasswordTitle').textContent = title || 'Password required';
            q(view, 'ebPasswordBody').textContent = message || '';
            q(view, 'ebPasswordInput').value = '';
            q(view, 'ebPasswordError').hidden = true;
            q(view, 'ebPasswordModal').hidden = false;
            q(view, 'ebPasswordInput').focus();
        });
    }

    function settlePassword(view, value) {
        var resolve = view.__ebPasswordResolve;
        var reject = view.__ebPasswordReject;
        view.__ebPasswordResolve = null;
        view.__ebPasswordReject = null;
        q(view, 'ebPasswordModal').hidden = true;
        if (value === undefined || value === null) {
            if (reject) { reject({ cancelled: true }); }
        } else if (resolve) {
            resolve(value);
        }
    }

    function submitPassword(view) {
        var value = q(view, 'ebPasswordInput').value;
        if (!value) {
            var error = q(view, 'ebPasswordError');
            error.textContent = 'Enter the password for this backup.';
            error.hidden = false;
            q(view, 'ebPasswordInput').focus();
            return;
        }
        settlePassword(view, value);
    }

    function confirmAction(view, title, body, okLabel, danger) {
        return new Promise(function (resolve) {
            view.__ebConfirmResolve = resolve;
            q(view, 'ebConfirmTitle').textContent = title || 'Are you sure?';
            q(view, 'ebConfirmBody').textContent = body || '';
            var ok = q(view, 'ebConfirmOk');
            ok.textContent = okLabel || 'Continue';
            ok.className = 'ebBtn ' + (danger ? 'ebDanger' : 'ebPrimary');
            q(view, 'ebConfirmModal').hidden = false;
            ok.focus();
        });
    }

    function settleConfirm(view, value) {
        var resolve = view.__ebConfirmResolve;
        view.__ebConfirmResolve = null;
        q(view, 'ebConfirmModal').hidden = true;
        if (resolve) {
            resolve(value);
        }
    }

    function checkForRestore(view, fileName, password) {
        return postJson('EmbyBackup/RestoreCheck', { FileName: fileName, Password: password || '' })
            .then(function (result) {
                if (result.PasswordRequired || result.WrongPassword) {
                    var message = result.WrongPassword
                        ? 'The password is incorrect. Try again.'
                        : (result.Error || 'This backup is protected by a password.');
                    return promptPassword(view, result.WrongPassword ? 'Wrong password' : 'Password required', message)
                        .then(function (entered) {
                            q(view, 'ebRestorePassword').value = entered;
                            return checkForRestore(view, fileName, entered);
                        });
                }
                return result;
            });
    }

    function startRestoreFlow(view, fileName) {
        if (!fileName) {
            toast('Choose a backup archive first.');
            return;
        }

        var button = q(view, 'ebRestoreStart');
        button.disabled = true;
        loading.show();

        checkForRestore(view, fileName, q(view, 'ebRestorePassword').value || '')
            .then(function (result) {
                loading.hide();
                if (!result.Success || !result.Valid) {
                    button.disabled = false;
                    showModal(view, 'This backup cannot be restored',
                        result.Error || 'The archive did not pass validation.');
                    return;
                }

                return confirmRestoreTwice(view, result).then(function (confirmed) {
                    if (!confirmed) {
                        button.disabled = false;
                        return;
                    }
                    beginRestore(view, result, q(view, 'ebRestorePassword').value || '');
                });
            }, function (error) {
                loading.hide();
                button.disabled = false;
                if (!error || !error.cancelled) {
                    toast('Could not check the backup.');
                }
            });
    }

    function confirmRestoreTwice(view, check) {
        var lines = [
            'Archive: ' + check.FileName,
            'Created: ' + fmtDate(check.CreatedUtc),
            'Emby version: ' + (check.EmbyVersion || '\u2014'),
            'Contents: ' + (check.FileCount || 0) + ' files / ' + (check.DatabaseCount || 0) + ' databases',
            'Uncompressed data: ' + fmtBytes(check.TotalBytes),
        ];
        if (check.Encrypted) {
            lines.push('Protection: password (AES-256)');
        }
        lines.push('');
        lines.push('This replaces Emby\'s server configuration and databases with the contents of this backup.');
        lines.push('The current Emby Backup settings (destination and credentials) are kept.');
        lines.push('Files created after the backup are not deleted.');
        lines.push('Emby must be restarted after the restore.');

        return confirmAction(view, 'Restore this backup?', lines.join('\n'), 'Continue', false)
            .then(function (ok) {
                if (!ok) {
                    return false;
                }

                return confirmAction(
                    view,
                    'Confirm the restore',
                    'This cannot be undone from this page. Emby keeps running while its data is replaced, '
                        + 'and you must restart it when the restore finishes.\n\n'
                        + 'Restore "' + check.FileName + '" now?',
                    'Restore now',
                    true);
            });
    }

    function beginRestore(view, check, password) {
        q(view, 'ebRestoreStart').disabled = true;
        q(view, 'ebRestartEmby').hidden = true;
        renderRestore(view, {
            Running: true,
            State: 'running',
            Progress: 0,
            Message: 'Starting the restore...',
            FileName: check.FileName
        });

        postJson('EmbyBackup/Restore', {
            FileName: check.FileName,
            Password: password || '',
            Confirm: true
        }).then(function (result) {
            if (!result.Started) {
                q(view, 'ebRestoreStart').disabled = false;
                renderRestore(view, result.Status || {
                    State: 'failed',
                    Message: result.Message,
                    Error: result.Message
                });
                toast(result.Message || 'Could not start the restore.');
                return;
            }

            renderRestore(view, result.Status || {});
            startRestorePolling(view);
        }, function () {
            q(view, 'ebRestoreStart').disabled = false;
            toast('Could not start the restore.');
        });
    }

    function renderRestore(view, status) {
        status = status || {};
        var running = !!status.Running;
        var hasRun = running || !!status.StartedUtc || status.State === 'success'
            || status.State === 'failed' || status.State === 'cancelled';

        var wrap = q(view, 'ebRestoreProgressWrap');
        wrap.hidden = !hasRun;
        q(view, 'ebRestoreBar').style.width = Math.max(0, Math.min(100, status.Progress || 0)) + '%';

        var message = q(view, 'ebRestoreMessage');
        message.hidden = !hasRun;
        if (hasRun) {
            var text = status.Message || stateText(status.State);
            if (status.Error && status.Error !== status.Message) {
                text += ' ' + status.Error;
            }
            if (status.State === 'success' && (status.RestoredFiles || status.RestoredDatabases)) {
                text += ' Restored ' + (status.RestoredFiles || 0) + ' files and '
                    + (status.RestoredDatabases || 0) + ' databases.';
            }
            message.textContent = text;
            message.className = status.State === 'failed'
                ? 'ebError ebSmall'
                : 'ebMuted ebSmall';
        }

        var warnings = q(view, 'ebRestoreWarnings');
        warnings.textContent = '';
        (status.Warnings || []).forEach(function (warning) {
            warnings.appendChild(el('div', 'ebWarn', warning));
        });

        var select = q(view, 'ebRestoreFile');
        var start = q(view, 'ebRestoreStart');
        start.disabled = running || !select.value || select.disabled;
        q(view, 'ebRestartEmby').hidden = !(status.State === 'success' && status.RestartRequired);
    }

    function loadRestoreStatus(view) {
        return ApiClient.getJSON(ApiClient.getUrl('EmbyBackup/RestoreStatus')).then(function (status) {
            renderRestore(view, status || {});
            return status;
        });
    }

    function startRestorePolling(view) {
        if (view.__ebRestoreTimer) {
            return;
        }

        view.__ebRestoreTimer = setInterval(function () {
            if (!view.isConnected || !view.getClientRects().length) {
                stopRestorePolling(view);
                return;
            }

            loadRestoreStatus(view).then(function (status) {
                if (!status.Running) {
                    stopRestorePolling(view);
                    refreshLists(view);
                    loadStatus(view);
                }
            }, function () { });
        }, 1000);
    }

    function stopRestorePolling(view) {
        if (view.__ebRestoreTimer) {
            clearInterval(view.__ebRestoreTimer);
            view.__ebRestoreTimer = null;
        }
    }

    function restartEmby(view) {
        confirmAction(
            view,
            'Restart Emby?',
            'Emby will restart now to load the restored data. This page will lose its connection for a moment; '
                + 'reload it when the server is back.',
            'Restart Emby',
            true).then(function (ok) {
                if (!ok) {
                    return;
                }

                q(view, 'ebRestartEmby').disabled = true;
                postJson('System/Restart', {}).then(function () {
                    toast('Emby is restarting...');
                    q(view, 'ebRestartEmby').hidden = true;
                    var message = q(view, 'ebRestoreMessage');
                    message.hidden = false;
                    message.className = 'ebMuted ebSmall';
                    message.textContent = 'Emby is restarting. Reload this page in a minute.';
                }, function () {
                    q(view, 'ebRestartEmby').disabled = false;
                    toast('Could not restart Emby. Restart it manually.');
                });
            });
    }

    function runBackup(view) {
        var button = q(view, 'ebRun');
        button.disabled = true;
        loading.show();

        saveSettings(view).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            return postJson('EmbyBackup/Run', {});
        }).then(function (result) {
            loading.hide();
            button.disabled = false;
            if (result.Status) {
                renderStatus(view, result.Status);
            }
            toast(result.Message || (result.Started ? 'Backup started.' : 'Could not start the backup.'));
            if (result.Started) {
                // A very small backup can finish before this response arrives, so reload the lists now as well;
                // the status poll keeps watching while it is still running.
                refreshLists(view);
                loadStatus(view);
            }
        }, function (error) {
            loading.hide();
            button.disabled = false;
            if (!error || !error.ebHandled) {
                toast('Could not start the backup.');
            }
        });
    }

    function testDestination(view) {
        var button = q(view, 'ebTest');
        button.disabled = true;
        loading.show();

        // Save first: the endpoint reads the stored configuration, so the test always checks what a
        // scheduled backup would actually use.
        saveSettings(view).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            return postJson('EmbyBackup/TestDestination', {});
        }).then(function (result) {
            loading.hide();
            button.disabled = false;
            var message = result.Message
                || (result.Success ? 'The destination is ready.' : 'The destination is not available.');
            if (result.Location) {
                message += '\n\n' + result.Location;
            }
            showModal(view, result.Success ? 'Destination ready' : 'Destination not available', message);
        }, function (error) {
            loading.hide();
            button.disabled = false;
            if (!error || !error.ebHandled) {
                toast('Could not test the destination.');
            }
        });
    }

    function cancelBackup(view) {
        q(view, 'ebCancel').disabled = true;
        postJson('EmbyBackup/Cancel', {}).then(function (result) {
            if (result.Status) {
                renderStatus(view, result.Status);
            }
            toast(result.Message || 'Cancelling...');
        }, function () {
            toast('Could not cancel the backup.');
        });
    }

    function refreshLists(view) {
        loadHistory(view).then(null, function () { });
        loadFiles(view).then(null, function () { });
    }

    Object.assign(View.prototype, BaseView.prototype);

    // Emby inserts a fresh copy of the page markup every time this view is shown and does not run the
    // controller constructor again for that copy, so handlers have to be attached to the current element on
    // every resume. The marker prevents a second resume on the same element from duplicating them.
    function bindHandlers(view) {
        if (view.__ebBound) {
            return;
        }
        view.__ebBound = true;

        view.querySelector('#ebSettingsForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            saveSettings(view).then(function (result) {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
            }, function (error) {
                loading.hide();
                if (!error || !error.ebHandled) {
                    toast('Saving failed.');
                }
            });
            return false;
        });

        [
            'ebDestination', 'ebNetworkPath', 'ebNetworkUser', 'ebNetworkPassword', 'ebNetworkDomain',
            'ebFtpHost', 'ebFtpPort', 'ebFtpPath', 'ebFtpUser', 'ebFtpPassword', 'ebFtpTls', 'ebFtpPassive',
            'ebFtpCert', 'ebRetention', 'ebCompression', 'ebBusySeconds', 'ebScheduled', 'ebVerify',
            'ebSchedulePeriod', 'ebScheduleDay', 'ebScheduleTime', 'ebProtect', 'ebBackupPassword'
        ].forEach(function (id) {
            var input = q(view, id);
            input.addEventListener('input', function () { setDirty(view, true); });
            input.addEventListener('change', function () { setDirty(view, true); });
        });

        q(view, 'ebProtect').addEventListener('change', function () {
            updateProtectionFields(view);
        });

        ['ebSchedulePeriod', 'ebScheduleDay', 'ebScheduleTime', 'ebScheduled'].forEach(function (id) {
            q(view, id).addEventListener('change', function () { updateScheduleFields(view); });
        });

        q(view, 'ebDestinationType').addEventListener('change', function () {
            setDirty(view, true);
            updateDestinationFields(view);
        });

        q(view, 'ebRun').addEventListener('click', function () { runBackup(view); });
        q(view, 'ebTest').addEventListener('click', function () { testDestination(view); });
        q(view, 'ebCancel').addEventListener('click', function () { cancelBackup(view); });
        q(view, 'ebRefresh').addEventListener('click', function () { refreshLists(view); });
        q(view, 'ebModalClose').addEventListener('click', function () { hideModal(view); });
        q(view, 'ebModal').addEventListener('click', function (e) {
            if (e.target === q(view, 'ebModal')) {
                hideModal(view);
            }
        });
        q(view, 'ebModal').addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                hideModal(view);
            }
        });

        q(view, 'ebRestoreStart').addEventListener('click', function () {
            startRestoreFlow(view, q(view, 'ebRestoreFile').value);
        });
        q(view, 'ebRestartEmby').addEventListener('click', function () { restartEmby(view); });
        q(view, 'ebRestoreFile').addEventListener('change', function () {
            updateRestorePasswordVisibility(view);
            q(view, 'ebRestoreStart').disabled = !q(view, 'ebRestoreFile').value;
        });

        q(view, 'ebConfirmCancel').addEventListener('click', function () { settleConfirm(view, false); });
        q(view, 'ebConfirmOk').addEventListener('click', function () { settleConfirm(view, true); });
        q(view, 'ebConfirmModal').addEventListener('click', function (e) {
            if (e.target === q(view, 'ebConfirmModal')) {
                settleConfirm(view, false);
            }
        });
        q(view, 'ebConfirmModal').addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                settleConfirm(view, false);
            }
        });

        q(view, 'ebPasswordCancel').addEventListener('click', function () { settlePassword(view, null); });
        q(view, 'ebPasswordOk').addEventListener('click', function () { submitPassword(view); });
        q(view, 'ebPasswordInput').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                submitPassword(view);
            }
        });
        q(view, 'ebPasswordModal').addEventListener('click', function (e) {
            if (e.target === q(view, 'ebPasswordModal')) {
                settlePassword(view, null);
            }
        });
        q(view, 'ebPasswordModal').addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                settlePassword(view, null);
            }
        });

        q(view, 'ebBrowse').addEventListener('click', function () { openBrowse(view, 'ebDestination'); });
        q(view, 'ebNetworkBrowse').addEventListener('click', function () { openBrowse(view, 'ebNetworkPath'); });
        q(view, 'ebBrowseClose').addEventListener('click', function () { closeBrowse(view); });
        q(view, 'ebBrowseUse').addEventListener('click', function () { useCurrentFolder(view); });
        q(view, 'ebBrowseUp').addEventListener('click', function () { goUp(view); });
        q(view, 'ebBrowseModal').addEventListener('click', function (e) {
            if (e.target === q(view, 'ebBrowseModal')) {
                closeBrowse(view);
            }
        });
        q(view, 'ebBrowseModal').addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                closeBrowse(view);
            }
        });

        // Emby binds its own Escape handling (back navigation) at the document level, so the
        // modal handlers above are not enough. A capture listener stops the event before Emby
        // sees it while one of our dialogs is open.
        document.addEventListener('keydown', function (e) {
            if (e.key !== 'Escape') {
                return;
            }
            // Emby may keep old page DOM around; only the visible page owns the dialog.
            if (!view.isConnected || !view.getClientRects().length) {
                return;
            }
            if (!q(view, 'ebBrowseModal').hidden) {
                e.preventDefault();
                e.stopPropagation();
                closeBrowse(view);
            } else if (!q(view, 'ebConfirmModal').hidden) {
                e.preventDefault();
                e.stopPropagation();
                settleConfirm(view, false);
            } else if (!q(view, 'ebPasswordModal').hidden) {
                e.preventDefault();
                e.stopPropagation();
                settlePassword(view, null);
            } else if (!q(view, 'ebModal').hidden) {
                e.preventDefault();
                e.stopPropagation();
                hideModal(view);
            }
        }, true);
    }

    function View() {
        BaseView.apply(this, arguments);
    }

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);

        var view = this.view;
        bindHandlers(view);
        var self = this;
        this._wasRunning = false;
        this._lastSignature = null;
        stopRestorePolling(view);

        loadSettings(view).then(null, function () {
            toast('Could not load the plugin settings.');
        });

        loading.show();
        loadStatus(view).then(function (status) {
            self._wasRunning = !!status.Running;
            self._lastSignature = statusSignature(status);
            loading.hide();
        }, function () {
            loading.hide();
            toast('Could not read the backup status.');
        });

        refreshLists(view);

        if (this._timer) {
            clearInterval(this._timer);
        }
        this._timer = setInterval(function () {
            loadStatus(view).then(function (status) {
                var wasRunning = self._wasRunning;
                self._wasRunning = !!status.Running;

                var signature = statusSignature(status);
                var changed = signature !== self._lastSignature;
                self._lastSignature = signature;

                if (!status.Running && (changed || wasRunning)) {
                    refreshLists(view);
                }
            }, function () { });
        }, 3000);
    };

    View.prototype.onPause = function () {
        if (this._timer) {
            clearInterval(this._timer);
            this._timer = null;
        }
        if (this.view) {
            stopRestorePolling(this.view);
        }
        BaseView.prototype.onPause.apply(this, arguments);
    };

    return View;
});
