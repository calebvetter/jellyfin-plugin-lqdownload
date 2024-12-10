"use strict";
var __awaiter = (this && this.__awaiter) || function (thisArg, _arguments, P, generator) {
    function adopt(value) { return value instanceof P ? value : new P(function (resolve) { resolve(value); }); }
    return new (P || (P = Promise))(function (resolve, reject) {
        function fulfilled(value) { try { step(generator.next(value)); } catch (e) { reject(e); } }
        function rejected(value) { try { step(generator["throw"](value)); } catch (e) { reject(e); } }
        function step(result) { result.done ? resolve(result.value) : adopt(result.value).then(fulfilled, rejected); }
        step((generator = generator.apply(thisArg, _arguments || [])).next());
    });
};
var TranscodeStatus;
(function (TranscodeStatus) {
    TranscodeStatus[TranscodeStatus["NotNeeded"] = 0] = "NotNeeded";
    TranscodeStatus[TranscodeStatus["CanTranscode"] = 1] = "CanTranscode";
    TranscodeStatus[TranscodeStatus["Queued"] = 2] = "Queued";
    TranscodeStatus[TranscodeStatus["Transcoding"] = 3] = "Transcoding";
    TranscodeStatus[TranscodeStatus["Completed"] = 4] = "Completed";
})(TranscodeStatus || (TranscodeStatus = {}));
let currentUrl = "";
let pageLoadTime = 0;
let currentItem = null;
// Initial load
onPageChange();
// Save the original methods
const originalPushState = history.pushState;
const originalReplaceState = history.replaceState;
// Override `pushState`
history.pushState = function (...args) {
    const result = originalPushState.apply(this, args);
    onPageChange();
    return result;
};
// Override `replaceState`
history.replaceState = function (...args) {
    const result = originalReplaceState.apply(this, args);
    onPageChange();
    return result;
};
// Listen for browser navigation
window.addEventListener("popstate", onPageChange);
// Listen for app command events
document.addEventListener("click", (e) => {
    const button = e.target.closest(".btnMoreCommands");
    if (button)
        onMoreButtonClick();
});
function getItemIdFromUrl() {
    const pseudoUrl = new URL(new URL(currentUrl).hash.replace("#", location.origin));
    return pseudoUrl.searchParams.get("id");
}
/**
 * When page changes
 */
function onPageChange() {
    if (location.href == currentUrl)
        return;
    currentUrl = location.href;
    pageLoadTime = Date.now();
    console.log("LQDOWNLOAD: page change", currentUrl);
    // Reset item
    currentItem = null;
    // Stop if not on details page
    if (!currentUrl.includes("/#/details"))
        return;
    // Get item ID from URL
    const itemId = getItemIdFromUrl();
    if (itemId) {
        // Create empty item
        currentItem = {
            id: itemId,
            status: TranscodeStatus.NotNeeded,
            transcodeProgress: 0,
            resolution: "",
        };
        getItemTranscodeStatus();
    }
}
/**
 * Load values for item
 */
function getItemTranscodeStatus() {
    return __awaiter(this, void 0, void 0, function* () {
        if (currentItem == null)
            return;
        console.log("LQDOWNLOAD: getItemTranscodeStatus for itemId", currentItem.id);
        const urlBase = location.href.split("web/#")[0];
        const url = `${urlBase}LQDownload/ClientScript/TranscodeStatus?itemId=${encodeURIComponent(currentItem.id)}`;
        try {
            const response = yield fetch(url, {
                method: "GET",
                headers: {
                    "Content-Type": "application/json",
                },
            });
            if (!response.ok) {
                console.error("LQDOWNLOAD: Failed to fetch transcode status. HTTP status:", response.status);
                return;
            }
            // Check that we're still on this item's page
            const urlItemId = getItemIdFromUrl();
            if (urlItemId != (currentItem === null || currentItem === void 0 ? void 0 : currentItem.id))
                return;
            const result = yield response.json();
            console.log("LQDOWNLOAD: Transcode status result:", result);
            const statusString = result.item.status;
            const statusEnumValue = TranscodeStatus[statusString];
            if (statusEnumValue !== undefined) {
                currentItem = Object.assign(Object.assign({}, result.item), { status: statusEnumValue });
            }
            updateUI();
        }
        catch (error) {
            console.error("LQDOWNLOAD: Error fetching transcode status:", error);
        }
    });
}
/**
 * Update the UI with progress or complete
 */
function updateUI() {
    if (currentItem == null)
        return;
    if (currentItem.status == TranscodeStatus.Queued || currentItem.status == TranscodeStatus.Transcoding) {
        // Pending or currently transcoding
        const selectVideoContainers = document.querySelectorAll("form.trackSelections .selectVideoContainer");
        if (selectVideoContainers.length == 0) {
            if (Date.now() - pageLoadTime < 10000) {
                // Retry main function at increments
                setTimeout(updateUI, 500);
            }
            else {
                console.warn("LQDOWNLOAD: selectVideoContainers not found within 10 seconds.");
            }
            // Stop further execution
            return;
        }
        // Add progress elements before each selectVideoContainer
        selectVideoContainers.forEach((container) => {
            var _a;
            if (!((_a = container.previousElementSibling) === null || _a === void 0 ? void 0 : _a.classList.contains("lqdownload-transcoding-progress"))) {
                const progressEl = createTranscodeProgressElement();
                if (progressEl)
                    container.insertAdjacentElement("beforebegin", progressEl.cloneNode(true));
            }
        });
        // Set the progress
        document.querySelectorAll(".lqdownload-transcoding-progress").forEach((el) => {
            if (currentItem == null)
                return; // For typescript
            const progressBar = el.querySelector(".lqdownload-progress-bar");
            if (progressBar instanceof HTMLElement)
                progressBar.style.width = `${currentItem.transcodeProgress}%`;
            const progressPercent = el.querySelector(".lqdownload-progress-percent");
            if (progressPercent)
                progressPercent.innerHTML = currentItem.transcodeProgress == 0 ? "Pending" : `${currentItem.transcodeProgress.toFixed(1)}%`;
        });
        // Refresh progress in 2 sec
        setTimeout(() => getItemTranscodeStatus(), 2000);
    }
    else if (currentItem.status == TranscodeStatus.Completed) {
        // Completed transcode
        // Update the UI to show completed
        document.querySelectorAll(".lqdownload-transcoding-progress").forEach((el) => {
            const progressBar = el.querySelector(".lqdownload-progress-bar");
            if (progressBar instanceof HTMLElement)
                progressBar.style.width = "100%";
            const progressPercent = el.querySelector(".lqdownload-progress-percent");
            if (progressPercent)
                progressPercent.innerHTML = "100%";
        });
    }
}
/**
 * When the More button is clicked on movie/show details page
 */
function onMoreButtonClick() {
    console.log("LQDOWNLOAD: onMoreButtonClick");
    const timeout = 1000; // 1 second timeout
    const startTime = performance.now();
    function waitForDownloadButton() {
        const dialogs = document.querySelectorAll(".dialogContainer .dialog.opened");
        const dialog = dialogs.length ? dialogs[dialogs.length - 1] : null;
        console.log("LQDOWNLOAD: ", dialog);
        if (dialog instanceof HTMLElement) {
            const downloadButton = dialog.querySelector('button[data-id="download"]');
            console.log("LQDOWNLOAD: ", downloadButton);
            if (downloadButton instanceof HTMLElement) {
                // This is a downloadable item.
                // Check for pre - transcoded file and display new download button
                addMenuButton(dialog, downloadButton);
                return;
            }
        }
        // Retry on the next frame
        if (performance.now() - startTime < timeout)
            requestAnimationFrame(waitForDownloadButton);
    }
    requestAnimationFrame(waitForDownloadButton);
}
/**
 * Adds transcode button to the menu, if applicable
 */
function addMenuButton(dialog, downloadButton) {
    return __awaiter(this, void 0, void 0, function* () {
        console.log("LQDOWNLOAD: addMenuButton");
        // If it doesn't need transcoding, or it already is transcoding, don't add button
        if (currentItem == null || (currentItem.status != TranscodeStatus.CanTranscode && currentItem.status != TranscodeStatus.Completed))
            return;
        console.log("LQDOWNLOAD: actually add menu button");
        // Right-align popup
        dialog.style.left = "unset";
        dialog.style.right = "10px";
        // Create button
        const newButton = document.createElement("button");
        newButton.setAttribute("is", "emby-button");
        newButton.setAttribute("type", "button");
        newButton.className = `listItem listItem-button actionSheetMenuItem emby-button ${isMobile() ? "actionsheet-xlargeFont" : ""}`;
        newButton.setAttribute("data-id", "download-lq");
        let icon = "video_settings";
        let text = `Create ${currentItem.resolution} Version`;
        let onClick = onTranscode;
        if (currentItem.status == TranscodeStatus.Completed) {
            icon = "file_download";
            text = `Download ${currentItem.resolution}`;
            onClick = onDownload;
        }
        // Create inner content for the button
        newButton.innerHTML = `
		<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ${icon}" aria-hidden="true"></span>
		<div class="listItemBody actionsheetListItemBody">
				<div class="listItemBodyText actionSheetItemText">${text}</div>
		</div>
	`;
        // Add the button click event listener
        newButton.addEventListener("click", onClick);
        // Insert the new button after the downloadButton in the DOM
        downloadButton.insertAdjacentElement("afterend", newButton);
    });
}
/**
 * Transcode button click
 */
function onTranscode() {
    return __awaiter(this, void 0, void 0, function* () {
        // Get item ID from URL
        if (currentItem == null)
            return;
        console.log("LQDOWNLOAD: transcode itemId", currentItem.id);
        currentItem.status = TranscodeStatus.Queued;
        const urlBase = location.href.split("web/#")[0];
        const url = `${urlBase}LQDownload/ClientScript/Transcode?itemId=${encodeURIComponent(currentItem.id)}`;
        try {
            const accessToken = getAccessToken();
            if (!accessToken)
                throw "No access token";
            const response = yield fetch(url, {
                method: "GET",
                headers: {
                    "Content-Type": "application/json",
                    Authorization: `MediaBrowser Token="${accessToken}"`,
                },
            });
            if (!response.ok) {
                console.error("LQDOWNLOAD: Failed to start transcode. HTTP status:", response.status);
                return;
            }
            const result = yield response.json();
            console.log("LQDOWNLOAD: Transcode start result:", result);
            updateUI();
        }
        catch (error) {
            console.error("LQDOWNLOAD: Error fetching transcode status:", error);
        }
    });
}
/**
 * Download button click with token-based URL
 */
function onDownload() {
    return __awaiter(this, void 0, void 0, function* () {
        if (currentItem == null)
            return;
        console.log("LQDOWNLOAD: Requesting download token for itemId", currentItem.id);
        const urlBase = location.href.split("web/#")[0];
        const tokenUrl = `${urlBase}LQDownload/ClientScript/GetToken?itemId=${encodeURIComponent(currentItem.id)}`;
        try {
            const accessToken = getAccessToken();
            if (!accessToken)
                throw "No access token";
            // Request download URL conatining short-lived token
            const response = yield fetch(tokenUrl, {
                method: "GET",
                headers: {
                    Authorization: `MediaBrowser Token="${accessToken}"`,
                },
            });
            if (!response.ok) {
                console.error("LQDOWNLOAD: Failed to get download URL. HTTP status:", response.status);
                return;
            }
            const { downloadUrl } = yield response.json();
            // Redirect the browser to the download URL
            location.href = downloadUrl;
            console.log("LQDOWNLOAD: Redirected to download URL");
        }
        catch (error) {
            console.error("LQDOWNLOAD: Error during download:", error);
        }
    });
}
/**
 * Creates an html element for transcode progress
 */
function createTranscodeProgressElement() {
    const tempContainer = document.createElement("div");
    tempContainer.innerHTML = `<div class="lqdownload-transcoding-progress" style="margin: 0 0 0.3em; display: flex; max-width: 44em">
			<div style="line-height: 1.75; margin: 0 0.2em 0 0; flex-basis: 6.25em; flex-grow: 0; flex-shrink: 0">Transcoding</div>
			<div style="position: relative; width: 100%; border-radius: 0.2em; overflow: hidden; background: #292929">
				<div class="lqdownload-progress-bar" style="width: 0; height: 100%; background: #007ea8; transition: width 2s"></div>
				<div class="lqdownload-progress-percent" style="position: absolute; top: 0; left: 0; right: 0; bottom: 0; display: flex; justify-content: center; align-items: center">0%</div>
			</div>
		</div>`;
    return tempContainer.firstElementChild;
}
/**
 * Gets an access token for authenticated API request.
 */
function getAccessToken() {
    var _a, _b;
    if (typeof ((_b = (_a = window.ApiClient) === null || _a === void 0 ? void 0 : _a._serverInfo) === null || _b === void 0 ? void 0 : _b.AccessToken) === "string")
        return window.ApiClient._serverInfo.AccessToken;
    const jellyfinCredentials = localStorage.getItem("jellyfin_credentials");
    if (!jellyfinCredentials)
        return;
    const credentials = JSON.parse(jellyfinCredentials);
    if (!credentials || !Array.isArray(credentials.Servers))
        return;
    // Match the server based on the current address
    const urlBase = new URL(location.href).origin;
    const server = credentials.Servers.find((s) => s.ManualAddress === urlBase || s.LocalAddress === urlBase);
    return server === null || server === void 0 ? void 0 : server.AccessToken;
}
/**
 * Gets whether the user is on mobile (function pulled from Jellyfin web)
 */
function isMobile() {
    const terms = ["mobi", "ipad", "iphone", "ipod", "silk", "gt-p1000", "nexus 7", "kindle fire", "opera mini"];
    const lower = navigator.userAgent.toLowerCase();
    for (let i = 0, length = terms.length; i < length; i++) {
        if (lower.indexOf(terms[i]) !== -1) {
            return true;
        }
    }
    return false;
}
