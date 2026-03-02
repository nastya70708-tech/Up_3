CREATE TABLE Client (
    userID INT PRIMARY KEY,
    fio VARCHAR(255) NOT NULL,
    phone VARCHAR(20) NOT NULL,
    login VARCHAR(100) UNIQUE NOT NULL,
    password VARCHAR(255) NOT NULL
);

CREATE TABLE Master (
    masterID INT PRIMARY KEY,
    fio VARCHAR(255) NOT NULL,
    phone VARCHAR(20) NOT NULL,
    login VARCHAR(100) UNIQUE NOT NULL,
    password VARCHAR(255) NOT NULL,
    type VARCHAR(100) NOT NULL
);

CREATE TABLE Requests (
    requestID INT PRIMARY KEY,
    startDate DATE NOT NULL,
    carType VARCHAR(100) NOT NULL,
    carModel VARCHAR(100) NOT NULL,
    problemDescryption TEXT NOT NULL,
    requestStatus VARCHAR(50) NOT NULL,
    completionDate DATE NULL,
    repairParts TEXT NULL,
    masterID INT NOT NULL,
    clientID INT NOT NULL,
    FOREIGN KEY (masterID) REFERENCES Master(masterID),
    FOREIGN KEY (clientID) REFERENCES Client(userID)
);

CREATE TABLE Comments (
    commentID INT PRIMARY KEY,
    message TEXT NOT NULL,
    masterID INT NOT NULL,
    requestID INT NOT NULL,
    FOREIGN KEY (masterID) REFERENCES Master(masterID),
    FOREIGN KEY (requestID) REFERENCES Requests(requestID)
);