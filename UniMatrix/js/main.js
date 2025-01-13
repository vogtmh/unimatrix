var matrix_server, matrix_user, matrix_user_id, matrix_password, matrix_device_id;
var serverurl;
var matrix_access_token = "";
var matrix_login_token = "";
var roomlist;
var roomnames = [];
var roommessages;
var setting_limitmessages;
var currentRoomId = "";
var currentRoomName = "";
var nextBatch = "";
var enableClientsync = 0;
var matrix_avatarlinks = [];

function loadSettings() {
    if (localStorage.getItem("matrix_server") === null) {
        console.log("matrix_server does not exist in localstorage. Creating ..");
        localStorage.matrix_server = "matrix.org";
        matrix_server = localStorage.matrix_server;
    } else {
        matrix_server = localStorage.matrix_server;
        console.log("matrix_server from localstorage: " + matrix_server);
    }

    if (localStorage.getItem("matrix_user") === null) {
        //console.log("matrix_user does not exist in localstorage.");
    } else {
        matrix_user = localStorage.matrix_user;
        console.log("matrix_user from localstorage: " + matrix_user);
    }

    if (localStorage.getItem("matrix_user_id") === null) {
        //console.log("matrix_user does not exist in localstorage.");
    } else {
        matrix_user_id = localStorage.matrix_user_id;
        console.log("matrix_user_id from localstorage: " + matrix_user_id);
    }

    if (localStorage.getItem("matrix_access_token") === null) {
        //console.log("matrix_accesskey does not exist in localstorage.");
    } else {
        matrix_access_token = localStorage.matrix_access_token;
        console.log("matrix_access_token from localstorage: " + matrix_access_token);
    }

    if (localStorage.getItem("matrix_password") === null) {
        //console.log("matrix_password does not exist in localstorage.");
    } else {
        matrix_password = localStorage.matrix_password;
        console.log("matrix_password from localstorage: " + matrix_password);
    }

    if (localStorage.getItem("matrix_login_token") === null) {
        //console.log("matrix_login_token does not exist in localstorage.");
    } else {
        matrix_login_token = localStorage.matrix_login_token;
        console.log("matrix_login_token from localstorage: " + matrix_login_token);
    }

    if (localStorage.getItem("matrix_device_id") === null) {
        //console.log("matrix_device_id does not exist in localstorage.");
    } else {
        matrix_device_id = localStorage.matrix_device_id;
        console.log("matrix_device_id from localstorage: " + matrix_device_id);
    }

    if (localStorage.getItem("setting_limitmessages") === null) {
        console.log("setting_limitmessages does not exist in localstorage. Creating ..");
        localStorage.setting_limitmessages = "30";
        setting_limitmessages = localStorage.setting_limitmessages;
    } else {
        setting_limitmessages = localStorage.setting_limitmessages;
        console.log("setting_limitmessages from localstorage: " + setting_limitmessages);
    }

    if (localStorage.getItem("matrix_avatarlinks") === null) {
        console.log("matrix_avatarlinks does not exist in localstorage.");
    } else {
        matrix_avatarlinks = JSON.parse(localStorage.matrix_avatarlinks);
        console.log("matrix_avatarlinks from localstorage: " + matrix_avatarlinks);
    }

    serverurl = "https://" + matrix_server;
}

function timeConverter(UNIX_timestamp) {
    var a = new Date(UNIX_timestamp);
    var year = a.getFullYear();
    var month = a.getMonth() + 1;
    if (month < 10) { month = '0' + month }
    var day = a.getDate();
    if (day < 10) { day = '0' + day }
    var hour = a.getHours();
    if (hour < 10) { hour = '0' + hour }
    var min = a.getMinutes();
    if (min < 10) { min = '0' + min }
    var time = year + '-' + month + '-' + day + ' ' + hour + ':' + min;
    return time;
}

function convertDIVname(name) {
    let result = name.replace(/!/g, "X");
    result = result.replace(/:/g, "C");
    result = result.replace(/\./g, "D");
    return result;
}

function getDiscoveryInformation() {
    let query = serverurl + "/.well-known/matrix/client";
    $("#activityicon").show();
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function getSupportedVersions() {
    let query = serverurl + "/_matrix/client/versions";
    $("#activityicon").show();
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function syncClient(since) {
    $("#syncled").css("background-color", "orange");
    var query = "";
    if (since == "" || since == undefined) {
        console.log("Running initial sync ..")
        query = serverurl + "/_matrix/client/v3/sync?access_token=" + matrix_access_token;
    }
    else {
        console.log("Running incremental sync ..")
        query = serverurl + "/_matrix/client/v3/sync?since=" + since + "&access_token=" + matrix_access_token;
    } 
    //$("#activityicon").show();
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            //$("#activityicon").hide();
            $("#syncled").css("background-color", "greenyellow");
            console.log(response);
            nextBatch = response.next_batch;
            console.log("Sync completed. next_batch=" + nextBatch);
            enableClientsync = 1;
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            //$("#activityicon").hide();
            $("#syncled").css("background-color", "red");
        },
    });
}

function checkLoginstate() {
    if (matrix_access_token != "") {
        authenticateUser();
    }
    else {
        showLoginmenu();
    }
}

function checkLogindata() {
    $("#login_notification").html("");

    let loginserver = $("#login_server").val();
    let loginuser   = $("#login_username").val();
    let loginpass   = $("#login_password").val();

    if (loginserver == "" || loginuser == "" || loginpass == "") {
        $("#login_notification").html("Information missing, please complete all inputs.")
    }
    else {
        matrix_server   = loginserver;
        matrix_user     = loginuser;
        matrix_password = loginpass;
        authenticateUser();
    }
}

function authenticateUser() {
    let query = serverurl + "/_matrix/client/v3/login";
    var querydata;
    $("#activityicon").show();

    console.log(matrix_access_token);
    if (matrix_access_token != "") {
        console.log("Already have an access token, login skipped.");
        authSuccess();
    }
    else {
        if (matrix_login_token == "") {
            console.log("Logging in with username/password ..")
            querydata = `{
                          "identifier": {
                            "type": "m.id.user",
                            "user": "`+ matrix_user + `"
                          },
                          "initial_device_display_name": "Jungle Phone",
                          "password": "`+ matrix_password + `",
                          "type": "m.login.password"
                     }`
        }
        else {
            console.log("Using token-based login ..")
            querydata = `{
                          "token": "` + matrix_access_token + `",
                          "type": "m.login.token"
                     }`
        }
        console.log(querydata);
        $.ajax({
            url: query,
            type: 'POST',
            data: querydata,
            dataType: 'json',
            success(response) {
                $("#activityicon").hide();
                matrix_access_token = response.access_token;
                matrix_device_id = response.device_id;
                matrix_user_id = response.user_id;

                localStorage.matrix_access_token = matrix_access_token;
                localStorage.matrix_device_id = matrix_device_id;
                localStorage.matrix_user_id = matrix_user_id;
                localStorage.matrix_user = matrix_user;
                localStorage.matrix_password = matrix_password;

                console.log(response);
                authSuccess();
            },
            error(jqXHR, status, errorThrown) {
                console.log('failed to fetch ' + query)
                $("#activityicon").hide();
                matrix_password = "";
                matrix_access_token = "";
                localStorage.matrix_password = "";
                localStorage.matrix_access_token = matrix_access_token;
                if ($("#loginmenu").css("display") != "none") {
                    $("#login_notification").html('Login failed, please check your username and password');
                }
            },
        });
    }
}

function authSuccess() {
    $("#header_text").html("[ " + matrix_user_id + " ]");
    if ($("#loginmenu").css("display") != "none") {
        $("#loginmenu").hide();
    }
    $("#login_server").val("matrix.org");
    $("#login_username").val("");
    $("#login_password").val("");
    $("#header_mainbutton").html('<img src="images/menu.png" onclick="toggleSidemenu()" />')
    getRoomlist();
}

function whoAmI() {
    let query = serverurl + "/_matrix/client/v3/account/whoami?access_token=" + matrix_access_token;
    $("#activityicon").show();
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function logoutUser() {
    matrix_access_token = '';
    localStorage.matrix_access_token = '';
    toggleSidemenu();
    loadSettings();
    checkLoginstate();
}

function getRoomlist() {
    let query = serverurl + "/_matrix/client/v3/joined_rooms?access_token=" + matrix_access_token;
    var querydata;
    $("#activityicon").show();
    
    console.log(querydata);
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            //console.log(response);
            roomlist = response.joined_rooms;
            roomitems = roomlist.length;
            for (let i = 0; i < roomitems; i++) {
                let roomId = roomlist[i]
                //getRoomalias(roomalias);
                getRoomname(roomId, roomitems);
            }
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function getRoomalias(roomId) {
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/aliases?access_token=" + matrix_access_token;
    var querydata;
    $("#activityicon").show();

    console.log(querydata);
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function getRoomname(roomId, roomLimit) {
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/state/m.room.name?access_token=" + matrix_access_token;
    $("#activityicon").show();

    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            //console.log(response);
            roomnames[roomId] = response.name;
            var nameitems = Object.keys(roomnames).length;
            if (nameitems == roomLimit) {
                printRoomnames();
            }
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function printRoomnames() {
    var nameitems = Object.keys(roomnames).length;
    let contenthtml = "";
    for (let i = 0; i < nameitems; i++) {
        let roomId = Object.keys(roomnames)[i]
        let roomName = Object.values(roomnames)[i]
        let divName = convertDIVname(roomId);
        contenthtml += `<div class="channel" onclick='openRoom("` + roomId + `")'>
                            <div class="channelavatar" id="avatar_` + divName + `"></div>
                            <div class="channelname">` + roomName + `</div>
                        </div>`;
    }
    contenthtml += "";
    $("#channellist").html(contenthtml);
    printAvatars();
}

function printAvatars() {
    var nameitems = Object.keys(roomnames).length;
    for (let i = 0; i < nameitems; i++) {
        let roomId = Object.keys(roomnames)[i]
        getRoomAvatar(roomId);
    }
}

function getRoomMessages(roomId) {
    let roomName = roomnames[roomId];
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/messages?access_token=" + matrix_access_token;
    $("#activityicon").show();

    $.ajax({
        url: query,
        type: 'GET',
        data: {
            dir: 'b',
            limit: setting_limitmessages,
        },
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
            roommessages = response;
            let messages = roommessages.chunk;
            let messagecount = messages.length;
            let roomhtml = '';
            for (let i = 0; i < messagecount; i++) {
                let messagecontent = messages[i].content;
                let messagetimestamp = messages[i].origin_server_ts;
                let sender = messages[i].sender;
                let ts = timeConverter(messagetimestamp);
                if (messagecontent.hasOwnProperty("msgtype")) {
                    if (messagecontent.msgtype == "m.text") {
                        roomhtml += `<div class="message">` + messagecontent.body + `<br/><div class="timestamp">` + sender + ` - ` + ts + `</div></div >`;
                    }
                    if (messagecontent.msgtype == "m.notice") {
                        roomhtml += `<div class="message">` + messagecontent.body + `<br/><div class="timestamp">` + sender + ` - ` + ts + `</div></div >`;
                    }
                }
                //console.log(messagecontent);
            }
            $("#roomcontent").html(roomhtml);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function getRoomAvatar(roomId) {
    let roomName = roomnames[roomId];
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/messages?access_token=" + matrix_access_token;
    var filter = '{"types":["m.room.avatar"]}';

    let avatarcount = matrix_avatarlinks.length;
    for (let a = 0; a < avatarcount; a++) {
        let avataritem = matrix_avatarlinks[a];
        if (avataritem.roomId == roomId) {
            let avatarlink = avataritem.link;
            setRoomAvatar(roomId, avatarlink);
            console.log("avatar from cache for " + roomId);
            return;
        }
    }

    $("#activityicon").show();
    $.ajax({
        url: query,
        type: 'GET',
        data: {
            dir: "b",
            limit: 1,
            filter, filter
        },
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            if (response.hasOwnProperty("chunk")) {
                if (response.chunk != null) {
                    try {
                        let mxclink = response.chunk[0].content.url;
                        let path = mxclink.slice(6);
                        let avatarlink = "https://matrix.org/_matrix/client/v1/media/download/" + path + "?access_token=" + matrix_access_token;
                        console.log(avatarlink);
                        setRoomAvatar(roomId, avatarlink);
                        let avataritem = {
                            roomId: roomId,
                            link: avatarlink
                        };

                        matrix_avatarlinks.push(avataritem);
                        localStorage.matrix_avatarlinks = JSON.stringify(matrix_avatarlinks);
                    }
                    catch (e) {
                        console.log("avatar skipped for " + roomId);
                    }
                }
                else {
                    console.log("No avatar events found in room " + roomId);
                }
            }
            else {
                console.log("No chunk item found in room " + roomId);
            }
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function setRoomAvatar(roomId, avatarlink) {
    let divName = convertDIVname(roomId);
    let divAvatar = "#avatar_" + divName;
    $(divAvatar).html(`<img src="` + avatarlink + `" />`);

    if (currentRoomId == roomId) {
        $("#header_avatar").html(`<img src="` + avatarlink + `" />`);
    }
}

function sendRoomMessage(roomId) {
    let message = $("#messageinput").val();
    if (roomId == "" || message == "") {
        return;
    }
    let transactionId = Date.now();
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/send/m.room.message/" + transactionId + "?access_token=" + matrix_access_token;
    $("#activityicon").show();

    $.ajax({
        url: query,
        type: 'PUT',
        data: JSON.stringify({
            body: message,
            msgtype: "m.text",
        }),
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
            getRoomMessages(roomId);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            console.log(status);
            $("#activityicon").hide();
        },
    });
}

function getRoomThreads(roomId) {
    let query = serverurl + "/_matrix/client/v1/rooms/" + roomId + "/threads?access_token=" + matrix_access_token;
    $("#activityicon").show();

    $.ajax({
        url: query,
        type: 'GET',
        data: {
            limit: 30,
        },
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").hide();
        },
    });
}

function openRoom(roomId) {
    let roomName = roomnames[roomId];
    currentRoomId = roomId;
    currentRoomName = roomName;
    getRoomAvatar(roomId);
    $("#header_text").html("[ " + roomName + " ]");
    let inputhtml = `<input type="text" id="messageinput">
                     <div id="messagebutton" onclick=sendRoomMessage("`+ roomId + `")>
                     <img src="images/send.png" /></div>`;
    $("#roominput").html(inputhtml);
    $("#room").show();
    $("#header_mainbutton").html('<img src="images/back.png" onclick="closeRoom()" />');
    let devicewidth = $(window).width();
    let buttonwidth = $("#messagebutton").width();
    let remainwidth = devicewidth - buttonwidth - 65;
    $("#messageinput").width(remainwidth);
    getRoomMessages(roomId);
}

function closeRoom() {
    currentRoomId = "";
    currentRoomName = "";
    $("#header_text").html("[ " + matrix_user_id + " ]");
    $("#header_avatar").html(``);
    $("#header_mainbutton").html('<img src="images/menu.png" onclick="toggleSidemenu()" />')
    $("#room").hide();
}

function openSettings() {
    var settingshtml = '';
    settingshtml += '<div class="settingitem">matrix_user_id: ' + matrix_user_id + '</div>';
    settingshtml += '<div class="settingitem">matrix_device_id: ' + matrix_device_id + '</div>';
    settingshtml += '<div class="settingitem">matrix_access_token: ' + matrix_access_token + '</div>';
    settingshtml += '<div class="settingitem">setting_limitmessages: ' + setting_limitmessages + '</div>';
    $("#header_text").html("[ Settings ]");
    $("#settingsmenu").html(settingshtml);
    $("#settingsmenu").show();
    toggleSidemenu();
    $("#header_mainbutton").html('<img src="images/back.png" onclick="closeSettings()" />')
}

function closeSettings() {
    $("#header_text").html("[ " + matrix_user_id + " ]");
    $("#header_mainbutton").html('<img src="images/menu.png" onclick="toggleSidemenu()" />')
    $("#settingsmenu").hide();
}

function toggleSidemenu() {
    if ($("#sidemenu").css("display") == "none") {
        $("#sidemenu").show();
    }
    else {
        $("#sidemenu").hide();
    }
}

function showLoginmenu() {
    var loginhtml = '';
    $("#header_text").html("[ Matrix Login ]");

    $('#login_password').keydown(function (event) {
        if (event.which === 13) {
            $("#login_password").blur()
            checkLogindata()
        }
    });

    $("#loginmenu").show();
    $("#header_mainbutton").html('')
}

function openCreateRoomDialog() {
    var createroomhtml = '';
    createroomhtml += `
                    <div id="input_customserver">
                        <table id="logintable">
                            <tr>
                                <td><span class="login_label">Room Name</span></td>
                                <td><input type="text" id="createroom_roomname" class="login_input" size="15" value="" /></td>
                            </tr>
                            <tr>
                                <td><span class="login_label">Visibility</span></td>
                                <td><input type="text" id="createroom_visibility" class="login_input" size="15" value="private" /></td>
                            </tr>
                        </table>
                        <div id="createroom_button" class="wbutton" onclick="createRoom()">Create</div>
                        <div id="createroom_notification"></div>
                    </div>
    `;
    $("#header_text").html("[ Create room ]");
    $("#createRoomDialog").html(createroomhtml);
    $("#createRoomDialog").show();
    toggleSidemenu();
    $("#header_mainbutton").html('<img src="images/back.png" onclick="closeCreateRoomDialog()" />')
}

function closeCreateRoomDialog() {
    $("#header_text").html("[ " + matrix_user_id + " ]");
    $("#header_mainbutton").html('<img src="images/menu.png" onclick="toggleSidemenu()" />')
    $("#createRoomDialog").hide();
}

function createRoom() {
    var preset;
    let newRoomname = $("#createroom_roomname").val();
    let newVisibility = $("#createroom_visibility").val();

    $("#createroom_notification").html("")
    if (newRoomname == "") {
        $("#createroom_notification").html("Room name cannot be empty")
        return;
    }
    if (newVisibility == "") {
        $("#createroom_notification").html("Visibility cannot be empty")
        return;
    }
    newRoomalias = newRoomname.replace(/ /g, "_").toLowerCase();
    
    if (newVisibility == "private") {
        preset = "private_chat";
    }
    else if (newVisibility == "public") {
        preset = "public_chat";
    }
    else {
        $("#createroom_notification").html("Visibility can only be public or private")
        return;
    }

    let query = serverurl + "/_matrix/client/v3/createRoom?access_token=" + matrix_access_token;
    $("#activityicon").show();

    $.ajax({
        url: query,
        type: 'POST',
        data: JSON.stringify({
            "name": newRoomname,
            "preset": preset,
            "room_alias_name": newRoomalias
        }),
        dataType: 'json',
        success(response) {
            $("#activityicon").hide();
            console.log(response);
            getRoomlist();
            closeCreateRoomDialog();
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            console.log(status);
            $("#activityicon").hide();
        },
    });
}

function showOnlinestate(status) {
    return;
}

function onBackPressed(event) {
    if ($('#room:visible').length > 0) {
        closeRoom();
        event.handled = true;
    }
    else if ($('#settingsmenu:visible').length > 0) {
        closeSettings();
        event.handled = true;
    }
    else if ($('#createRoomDialog:visible').length > 0) {
        closeCreateRoomDialog();
        event.handled = true;
    }
    else if ($('#sidemenu:visible').length > 0) {
        toggleSidemenu();
        event.handled = true;
    }
}

function TimerRun() {
    if (currentRoomId != "") {
        getRoomMessages(currentRoomId);
        console.log(`Checking for updates in "` + currentRoomName + `"`);
    }
    if (enableClientsync == 1) {
        syncClient(nextBatch);
    }
}

$(document).ready(function () {
    try {
        Windows.UI.Core.SystemNavigationManager.getForCurrentView().addEventListener("backrequested", onBackPressed);
        appVersion = Windows.ApplicationModel.Package.current.id.version;
        appString = `v${appVersion.major}.${appVersion.minor}.${appVersion.build}`;
        $("#version").html("&nbsp;" + appString);
    }
    catch (e) {
        console.log('Windows namespace not available, backbutton listener and versioninfo skipped.')
        appString = '';
    }

    window.addEventListener('online', () => showOnlinestate("online"));
    window.addEventListener('offline', () => showOnlinestate("offline"));

    document.onselectstart = new Function("return false")

    loadSettings();
    checkLoginstate();
    //syncClient(nextBatch);
});

setInterval(TimerRun, 15000);