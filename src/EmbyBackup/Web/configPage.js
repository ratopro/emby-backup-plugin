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
            q(view, 'ebScheduled').checked = config.ScheduledBackupsEnabled !== false;
            q(view, 'ebVerify').checked = config.VerifyAfterCreate !== false;
            updateDestinationFields(view);
            setDirty(view, false);
        });
    }

    function saveSettings(view) {
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
            config.ScheduledBackupsEnabled = q(view, 'ebScheduled').checked;
            config.VerifyAfterCreate = q(view, 'ebVerify').checked;

            return ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                setDirty(view, false);
                return result;
            });
        });
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

        q(view, 'ebRun').disabled = !!status.Running;
        q(view, 'ebCancel').disabled = !status.Running;
        q(view, 'ebRunHint').textContent = status.Running
            ? 'A backup is running. You can keep this page open or close it; the server continues.'
            : 'The settings are saved automatically before a manual run.';
        if (status.PluginVersion) {
            q(view, 'ebPluginVersion').textContent = 'Plugin ' + status.PluginVersion;
        }
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
            row.appendChild(el('td', null, file.HasChecksumFile ? 'Present' : 'Missing'));

            var actions = el('td');
            var verify = el('button', 'ebBtn ebSmall', 'Verify');
            verify.type = 'button';
            verify.addEventListener('click', function () {
                verifyFile(view, file.FileName);
            });
            actions.appendChild(verify);
            row.appendChild(actions);
            body.appendChild(row);
        });
    }

    function loadStatus(view) {
        return ApiClient.getJSON(ApiClient.getUrl('EmbyBackup/Status')).then(function (status) {
            renderStatus(view, status);
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

    function verifyFile(view, fileName) {
        postJson('EmbyBackup/Verify', { FileName: fileName }).then(function (result) {
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
        }, function () {
            loading.hide();
            button.disabled = false;
            toast('Could not start the backup.');
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
        }, function () {
            loading.hide();
            button.disabled = false;
            toast('Could not test the destination.');
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
            }, function () {
                loading.hide();
                toast('Saving failed.');
            });
            return false;
        });

        [
            'ebDestination', 'ebNetworkPath', 'ebNetworkUser', 'ebNetworkPassword', 'ebNetworkDomain',
            'ebFtpHost', 'ebFtpPort', 'ebFtpPath', 'ebFtpUser', 'ebFtpPassword', 'ebFtpTls', 'ebFtpPassive',
            'ebFtpCert', 'ebRetention', 'ebCompression', 'ebBusySeconds', 'ebScheduled', 'ebVerify'
        ].forEach(function (id) {
            var input = q(view, id);
            input.addEventListener('input', function () { setDirty(view, true); });
            input.addEventListener('change', function () { setDirty(view, true); });
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
        BaseView.prototype.onPause.apply(this, arguments);
    };

    return View;
});
