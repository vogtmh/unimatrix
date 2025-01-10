var matrix_server, matrix_user, matrix_user_id, matrix_password, matrix_device_id;
var serverurl;
var matrix_access_token = "";
var matrix_login_token = "";
var roomlist;
var roomnames = [];
var roommessages;
var setting_limitmessages;
var currentRoomId;
var currentRoomname;

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

function getDiscoveryInformation() {
    let query = serverurl + "/.well-known/matrix/client";
    $("#activityicon").html('<img src="images/activity_on.gif" />');
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function getSupportedVersions() {
    let query = serverurl + "/_matrix/client/versions";
    $("#activityicon").html('<img src="images/activity_on.gif" />');
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
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
    $("#activityicon").html('<img src="images/activity_on.gif" />');

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
                $("#activityicon").html('<img src="images/activity_off.png" />');
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
                $("#activityicon").html('<img src="images/activity_off.png" />');
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
    $("#activityicon").html('<img src="images/activity_on.gif" />');
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
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
    $("#activityicon").html('<img src="images/activity_on.gif" />');
    
    console.log(querydata);
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
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
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function getRoomalias(roomId) {
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/aliases?access_token=" + matrix_access_token;
    var querydata;
    $("#activityicon").html('<img src="images/activity_on.gif" />');

    console.log(querydata);
    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function getRoomname(roomId, roomLimit) {
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/state/m.room.name?access_token=" + matrix_access_token;
    $("#activityicon").html('<img src="images/activity_on.gif" />');

    $.ajax({
        url: query,
        type: 'GET',
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            //console.log(response);
            roomnames[roomId] = response.name;
            var nameitems = Object.keys(roomnames).length;
            if (nameitems == roomLimit) {
                printRoomnames();
            }
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function printRoomnames() {
    var nameitems = Object.keys(roomnames).length;
    let contenthtml = "";
    for (let i = 0; i < nameitems; i++) {
        let roomId = Object.keys(roomnames)[i]
        let roomName = Object.values(roomnames)[i]
        contenthtml += `<div class="channel" onclick='getRoomMessages("` + roomId + `")'>` + roomName + "</div>";
    }
    contenthtml += "";
    $("#channellist").html(contenthtml);
}

function getRoomMessages(roomId) {
    let roomName = roomnames[roomId];
    currentRoomId = roomId;
    currentRoomName = roomName;
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/messages?access_token=" + matrix_access_token;
    $("#activityicon").html('<img src="images/activity_on.gif" />');

    $.ajax({
        url: query,
        type: 'GET',
        data: {
            dir: 'b',
            limit: setting_limitmessages,
        },
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
            roommessages = response;
            let messages = roommessages.chunk;
            let messagecount = messages.length;
            let roomhtml = '';
            let inputhtml = `<input type="text" id="messageinput">
                            <div id="messagebutton" onclick=sendRoomMessage("`+ roomId + `")>
                            <img src="images/send.png" /></div>`;
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
            $("#roominput").html(inputhtml);
            let devicewidth = $(window).width();
            let buttonwidth = $("#messagebutton").width();
            let remainwidth = devicewidth - buttonwidth - 65;
            $("#messageinput").width(remainwidth);
            openRoom(roomId);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function sendRoomMessage(roomId) {
    let message = $("#messageinput").val();
    if (message == "") {
        return;
    }
    let transactionId = Date.now();
    let query = serverurl + "/_matrix/client/v3/rooms/" + roomId + "/send/m.room.message/" + transactionId + "?access_token=" + matrix_access_token;
    $("#activityicon").html('<img src="images/activity_on.gif" />');

    $.ajax({
        url: query,
        type: 'PUT',
        data: JSON.stringify({
            body: message,
            msgtype: "m.text",
        }),
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
            getRoomMessages(roomId);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            console.log(status);
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function getRoomThreads(roomId) {
    let query = serverurl + "/_matrix/client/v1/rooms/" + roomId + "/threads?access_token=" + matrix_access_token;
    $("#activityicon").html('<img src="images/activity_on.gif" />');

    $.ajax({
        url: query,
        type: 'GET',
        data: {
            limit: 30,
        },
        dataType: 'json',
        success(response) {
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            $("#activityicon").html('<img src="images/activity_off.png" />');
        },
    });
}

function openRoom(roomId) {
    let roomName = roomnames[roomId];
    $("#header_text").html("[ " + roomName + " ]");
    $("#room").show();
    $("#header_mainbutton").html('<img src="images/back.png" onclick="closeRoom()" />')
}

function closeRoom() {
    $("#header_text").html("[ " + matrix_user_id + " ]");
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
    $("#activityicon").html('<img src="images/activity_on.gif" />');

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
            $("#activityicon").html('<img src="images/activity_off.png" />');
            console.log(response);
            getRoomlist();
            closeCreateRoomDialog();
        },
        error(jqXHR, status, errorThrown) {
            console.log('failed to fetch ' + query)
            console.log(status);
            $("#activityicon").html('<img src="images/activity_off.png" />');
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
    else if ($('#sidemenu:visible').length > 0) {
        toggleSidemenu();
        event.handled = true;
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
});